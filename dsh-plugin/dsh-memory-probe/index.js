// dsh-memory-probe —— 「dsh 管理面板」关键假设验证探针
//
// 验证目标：挂在 profile 层的自定义 dsh 插件，能否收到由人格卡（agent preset）
// 驱动的 agent 会话事件（agent/pre-step），并具备改写本轮消息（注入）的能力。
//
// 设计约束：探针本身绝不能影响 dsh 主流程 —— 所有日志写入、事件注册都用
// try/catch 兜底，apply() 整体也被包住。
import { appendFileSync } from 'node:fs'

// dsh HOME 与日志文件的绝对路径。
// 默认取 %USERPROFILE%\.dsh，可用 DSH_MEMORY_PROBE_HOME 覆盖；
// Windows 路径含反斜杠，用 String.raw 避免 JS 转义问题。
const DSH_HOME = process.env.DSH_MEMORY_PROBE_HOME || String.raw`C:\Users\Public\.dsh`
const LOG_PATH = process.env.DSH_MEMORY_PROBE_LOG || DSH_HOME + String.raw`\dsh-memory-probe.log`

/** Cordis 插件名。 */
export const name = 'dsh-memory-probe'
/** 不依赖任何服务，插件可立即 apply。 */
export const inject = []

/**
 * 统一日志出口：UTF-8 追加、带 ISO 时间戳。
 * 任何异常都被吞掉，保证探针不会因写日志失败而拖垮 dsh。
 */
function log(tag, data) {
  try {
    const body = typeof data === 'string' ? data : JSON.stringify(data)
    appendFileSync(LOG_PATH, `[${new Date().toISOString()}] [${tag}] ${body}\n`, 'utf8')
  } catch {
    // 静默：日志写入失败也不得抛出
  }
}

/**
 * 注册一个观测监听器并记录结果，用于探测插件能看见的事件面。
 * 事件名不存在时 Cordis 不会报错，这里仍用 try/catch 兜底。
 */
function observe(ctx, eventName, extract) {
  try {
    ctx.on(eventName, (...args) => {
      try {
        log(`${eventName}.hit`, extract ? extract(...args) : { argc: args.length })
      } catch (error) {
        log(`${eventName}.hit.error`, String(error))
      }
    }, { global: true })
    log('listen.ok', eventName)
  } catch (error) {
    log('listen.error', { eventName, error: String(error) })
  }
}

/**
 * 插件入口。
 * @param ctx - Cordis 上下文（profile 根上下文）。
 */
export function apply(ctx) {
  try {
    log('apply', 'dsh-memory-probe apply() 被调用 —— 插件已被加载')

    // 打印 ctx 上可见的键，帮助判断可用的服务/事件面
    try {
      log('ctx.keys', Object.keys(ctx))
    } catch (error) {
      log('ctx.keys.error', String(error))
    }

    // ── 核心：agent/pre-step（waterfall） ───────────────────────────────
    // global: true 绕过上下文作用域筛选，确保能收到各 agent 作用域内发出的事件。
    // 调 next() 拿到上游 decision 后原样返回，只观测、不改写（验证阶段最低副作用）。
    ctx.on('agent/pre-step', async (payload, next) => {
      log('agent/pre-step.hit', {
        step: payload?.step,
        turn: payload?.turn,
        messages: payload?.messages?.length,
        agentId: payload?.agent?.id ?? null
      })
      const decision = await next()
      log('agent/pre-step.decision', {
        kind: decision?.kind,
        messages: decision?.messages?.length
      })
      return decision
    }, { global: true })
    log('listen.ok', 'agent/pre-step')

    // ── 额外观测监听器：探测事件面 ─────────────────────────────────────
    observe(ctx, 'agent/created')
    observe(ctx, 'agent/turn-end', (agent, reason) => ({ reason: String(reason) }))
    observe(ctx, 'session/created', (session) => ({ sessionId: session?.id ?? null }))
    observe(ctx, 'session/event', (session, event) => ({ eventType: event?.type }))
  } catch (error) {
    log('apply.error', String(error?.stack ?? error))
  }
}
