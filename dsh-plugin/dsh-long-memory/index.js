// dsh-long-memory —— 大容量长期记忆注入插件
//
// 作用：独立于「人格卡 / 工作区指令」，在每轮对话的第 1 步把用户自定义的
// 记忆文件（默认 <DSH_HOME>/MEMORY.md）注入为一则 user 消息，让模型获得
// 跨会话的长期背景知识。
//
// 设计要点：
//  - 独立 source.kind（'long-memory'）：绝不能复用 agent-instructions，
//    否则会与人格卡/工作区指令的 inbox 去重逻辑互相删除。
//  - 只在 step === 1 注入：append-only，对 KV Cache 友好。
//  - 字节预算独立于人格卡的 64KB，默认 256KB，可通过 settings.yaml 覆盖。
//  - 任何异常都不得影响 dsh 主流程，全部 try/catch 兜底。
import { appendFileSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import z from '@deepseek-ai/schemastery'
import { createUserMessage } from '@deepseek-ai/dsh-llm'
import { resolveDshHome } from '@deepseek-ai/dsh-home-paths'

/** Cordis 插件名。 */
export const name = 'long-memory'
/** 不强制依赖任何服务：settings 为可选依赖，缺失时降级为组合配置。 */
export const inject = []

/** settings.yaml 中的配置命名空间。 */
const SETTINGS_NS = 'long-memory'
/** 记忆文件默认字节预算（256KB，独立于人格卡 64KB 预算）。 */
const DEFAULT_MAX_BYTES = 262144
/** 单文件读取上限，超过则完全跳过（避免读入巨型文件）。 */
const DEFAULT_MAX_SOURCE_BYTES = 2097152
/** system-reminder 框架标记。 */
const SYSTEM_REMINDER_OPEN = '<system-reminder>'
const SYSTEM_REMINDER_CLOSE = '</system-reminder>'
/** 注入内容对模型的说明文案。 */
const INTRO = 'The following long-term memory was saved by the user for this machine. Use it as background knowledge when relevant. It does not override system, developer, or direct user instructions.'

/** 插件配置 schema；schema 默认值 → 组合 base → 用户 settings.yaml 层层覆盖。 */
export const Config = z.object({
  /** 记忆文件绝对路径；缺省为 <DSH_HOME>/MEMORY.md。 */
  file: z.string(),
  /** 注入正文的 UTF-8 字节上限。 */
  maxBytes: z.number().step(1).min(0).default(DEFAULT_MAX_BYTES),
  /** 单次读取文件的字节上限，超过则跳过。 */
  maxSourceBytes: z.number().step(1).min(1).default(DEFAULT_MAX_SOURCE_BYTES),
  /** 总开关。 */
  enabled: z.boolean().default(true)
})

/** 计算文本的 UTF-8 字节长度。 */
function byteLength(value) {
  return Buffer.byteLength(value, 'utf8')
}

/**
 * UTF-8 安全截断：保证不切裂多字节字符。
 * @param value - 原始文本。
 * @param maxBytes - 允许的最大字节数。
 * @returns 截断后的文本。
 */
function truncateUtf8(value, maxBytes) {
  const bytes = Buffer.from(value, 'utf8')
  if (bytes.length <= maxBytes) return value
  let end = Math.max(0, Math.trunc(maxBytes))
  // 回退到 UTF-8 字符起始字节（0b10xxxxxx 为续字节）
  while (end > 0 && (bytes.readUInt8(end) & 192) === 128) end -= 1
  return bytes.subarray(0, end).toString('utf8')
}

/**
 * 转义正文里字面的 </system-reminder>，防止记忆内容提前闭合框架。
 * 同时兼容大小写与多余空白。
 */
function escapeFrameBody(body) {
  return body.replace(/<\/\s*system-reminder\s*>/gi, '<\\/system-reminder>')
}

/** 日志出口：写入 <DSH_HOME>/dsh-long-memory.log；失败一律静默。 */
function log(tag, data) {
  try {
    const body = typeof data === 'string' ? data : JSON.stringify(data)
    const path = join(resolveDshHome(), 'dsh-long-memory.log')
    appendFileSync(path, `[${new Date().toISOString()}] [${tag}] ${body}\n`, 'utf8')
  } catch {
    // 日志失败不得影响主流程
  }
}

/**
 * 补齐默认值，把可能残缺的配置对象规范化为运行时配置。
 * @param raw - settings 解析值或组合配置。
 * @returns 规范化后的配置。
 */
function normalizeConfig(raw) {
  const source = raw && typeof raw === 'object' ? raw : {}
  return {
    file: typeof source.file === 'string' && source.file.length > 0 ? source.file : undefined,
    maxBytes: Number.isFinite(source.maxBytes) ? source.maxBytes : DEFAULT_MAX_BYTES,
    maxSourceBytes: Number.isFinite(source.maxSourceBytes) ? source.maxSourceBytes : DEFAULT_MAX_SOURCE_BYTES,
    enabled: source.enabled !== false
  }
}

/**
 * 按 mtime + size 缓存文件内容，避免每一步都读盘。
 * 缓存只保留「最后一次成功读取」的内容；文件变更后自动失效。
 */
const fileCache = { path: '', mtimeMs: -1, size: -1, text: undefined }

/**
 * 读取记忆文件（带轻量缓存）。
 * @param path - 绝对路径。
 * @param maxSourceBytes - 单文件字节上限。
 * @returns 文件文本；不存在 / 为空 / 超限时为 undefined。
 */
function readMemoryFile(path, maxSourceBytes) {
  let stat
  try {
    stat = statSync(path)
  } catch {
    return undefined // 文件不存在或无权限：不注入
  }
  if (!stat.isFile()) return undefined
  if (stat.size > maxSourceBytes) {
    log('file.too-large', { path, size: stat.size, maxSourceBytes })
    return undefined
  }
  if (fileCache.path === path && fileCache.mtimeMs === stat.mtimeMs && fileCache.size === stat.size) {
    return fileCache.text // 命中缓存，未改动
  }
  let text
  try {
    text = readFileSync(path, 'utf8')
  } catch (error) {
    log('file.read.error', { path, error: String(error) })
    return undefined
  }
  fileCache.path = path
  fileCache.mtimeMs = stat.mtimeMs
  fileCache.size = stat.size
  fileCache.text = text
  return text
}

/**
 * 构造注入用的 user 消息。
 * @param raw - 记忆正文。
 * @param config - 运行时配置。
 * @returns 消息文本。
 */
function renderMemoryMessage(raw, config) {
  const overBudget = byteLength(raw) > config.maxBytes
  const body = overBudget
    ? `${truncateUtf8(raw, config.maxBytes)}\n\n[已截断：记忆内容超过当前上限 ${config.maxBytes} 字节（UTF-8），仅注入前 ${config.maxBytes} 字节。]`
    : raw
  // 先截断再转义，保证转义不会破坏预算
  const escaped = escapeFrameBody(body)
  return [
    SYSTEM_REMINDER_OPEN,
    INTRO,
    '',
    '<long-term-memory>',
    escaped,
    '</long-term-memory>',
    SYSTEM_REMINDER_CLOSE
  ].join('\n')
}

/**
 * 插件入口。
 * @param ctx - Cordis 上下文。
 * @param config - 组合层（cordis patch / 包内补丁）传入的配置。
 */
export function apply(ctx, config) {
  try {
    log('plugin.load', { name, pid: process.pid, dshHome: resolveDshHome() })

    // ── 配置来源：settings 服务可用时注册命名空间并读取解析值 ────────────────
    let scope
    const attachSettings = (serviceCtx) => {
      try {
        scope = serviceCtx.settings.register(SETTINGS_NS, Config, { base: config })
        // 记录 base 与「base + 用户 settings.yaml」解析后的生效值，便于排查
        log('settings.registered', {
          ns: SETTINGS_NS,
          base: normalizeConfig(config),
          resolved: normalizeConfig(scope.get())
        })
      } catch (error) {
        log('settings.register.error', String(error?.stack ?? error))
      }
    }
    try {
      // settings 可能晚于本插件就绪：先尝试直接取服务，取不到再退化为延迟注入
      const direct = typeof ctx.get === 'function' ? ctx.get('settings') : undefined
      log('settings.probe', { hasGet: typeof ctx.get === 'function', hasService: Boolean(direct) })
      if (direct) attachSettings(ctx)
      else ctx.inject(['settings'], (serviceCtx) => attachSettings(serviceCtx))
    } catch (error) {
      log('settings.inject.unavailable', String(error))
      attachSettings(ctx)
    }

    /** 读取当前生效配置：settings 解析值优先，缺失时回退组合配置。 */
    const readConfig = () => {
      try {
        if (scope) return normalizeConfig(scope.get())
        const service = typeof ctx.get === 'function' ? ctx.get('settings') : undefined
        const resolved = service?.get?.(SETTINGS_NS)
        if (resolved) return normalizeConfig(resolved)
      } catch (error) {
        log('settings.read.error', String(error))
      }
      return normalizeConfig(config)
    }

    // ── 核心：agent/pre-step 注入 ───────────────────────────────────────
    // global: true 才能收到人格卡（agent preset）作用域内发出的事件。
    ctx.on('agent/pre-step', async (payload, next) => {
      const decision = await next()
      try {
        // 只在第 1 步注入：append-only、天然幂等
        if (payload?.step !== 1) return decision
        const resolved = readConfig()
        if (!resolved.enabled) return decision
        const path = resolved.file ?? join(resolveDshHome(), 'MEMORY.md')
        const raw = readMemoryFile(path, resolved.maxSourceBytes)
        if (raw === undefined || raw.trim().length === 0) return decision
        const text = renderMemoryMessage(raw, resolved)
        const injected = createUserMessage({
          content: [{ type: 'text', text }],
          // kind 必须是本插件专属，绝不能复用 agent-instructions
          source: { kind: 'long-memory', form: 'memory' }
        })
        log('inject', {
          turn: payload?.turn,
          step: payload?.step,
          file: path,
          maxBytes: resolved.maxBytes,
          sourceBytes: byteLength(raw),
          injectedBytes: byteLength(text),
          truncated: byteLength(raw) > resolved.maxBytes
        })
        return { ...decision, messages: [...decision.messages, injected] }
      } catch (error) {
        // 注入失败不得影响 dsh 运行
        log('inject.error', String(error?.stack ?? error))
        return decision
      }
    }, { global: true })
    log('listen.ok', 'agent/pre-step')
  } catch (error) {
    log('apply.error', String(error?.stack ?? error))
  }
}
