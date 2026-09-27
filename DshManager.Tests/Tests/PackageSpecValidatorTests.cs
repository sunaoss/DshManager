using System.Collections.Generic;
using DshManager.Services;

namespace DshManager.Tests;

/// <summary>
/// PackageSpecValidator 单元测试：覆盖市场页（包名来自 registry JSON）与插件页（手输）
/// 两条安装路径共用的注入防线。这些用例就是「pnpm 选项注入」的攻击形状。
/// </summary>
public class PackageSpecValidatorTests
{
    // ==================== IsSafePackageName ====================

    [Theory]
    [InlineData("dsh-cost-meter")]
    [InlineData("dsh-memory")]
    [InlineData("@deepseek-ai/dsh")]
    [InlineData("@furongjun1999/dsh-memory")]
    [InlineData("a")]
    [InlineData("pkg.name_1~x")]
    public void IsSafePackageName_AcceptsNormalNames(string name)
        => Assert.True(PackageSpecValidator.IsSafePackageName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo --registry=http://evil")]     // 空格后接选项 = pnpm 参数注入
    [InlineData("--registry=http://evil")]         // 纯选项
    [InlineData("foo;rm -rf /")]                   // shell 元字符
    [InlineData("foo&calc")]
    [InlineData("foo|whoami")]
    [InlineData("foo\"bar")]
    [InlineData("foo'bar")]
    [InlineData("foo`bar`")]
    [InlineData("foo$(whoami)")]
    [InlineData("../evil")]                        // 路径穿越
    [InlineData("/abs/path")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("@scope//name")]                   // 作用域包斜杠过多
    [InlineData("@scope/name/extra")]              // 非作用域包不允许斜杠
    [InlineData("/leading")]
    [InlineData("trailing/")]
    [InlineData("@")]
    [InlineData(" foo")]                           // 前导空格：trim 后会被切开
    [InlineData("foo ")]                           // 尾随空格
    public void IsSafePackageName_RejectsInjectionShapes(string? name)
        => Assert.False(PackageSpecValidator.IsSafePackageName(name));

    [Fact]
    public void IsSafePackageName_RejectsOverlongName()
        => Assert.False(PackageSpecValidator.IsSafePackageName(new string('a', 215)));

    [Fact]
    public void IsSafePackageName_AcceptsMaxLengthName()
        => Assert.True(PackageSpecValidator.IsSafePackageName(new string('a', 214)));

    // ==================== IsSafeVersion ====================

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.9.7")]
    [InlineData("^1.0.0")]
    [InlineData("~0.6.0")]
    [InlineData(">1.0.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0+build.5")]
    [InlineData("latest")]
    [InlineData("next")]
    public void IsSafeVersion_AcceptsNormalVersions(string version)
        => Assert.True(PackageSpecValidator.IsSafeVersion(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1.2.3 --registry=http://evil")]   // 版本里塞选项
    [InlineData("1.2.3;rm -rf /")]
    [InlineData("1.2.3\"x")]
    [InlineData("1.2.3|x")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3/../../etc")]
    public void IsSafeVersion_RejectsInjectionShapes(string? version)
        => Assert.False(PackageSpecValidator.IsSafeVersion(version));

    // ==================== TryValidateSpec ====================

    [Fact]
    public void TryValidateSpec_PlainName()
    {
        Assert.True(PackageSpecValidator.TryValidateSpec(
            "dsh-cost-meter", out var name, out var version, out var separator));
        Assert.Equal("dsh-cost-meter", name);
        Assert.Equal("", version);
        Assert.Equal("", separator);
    }

    [Fact]
    public void TryValidateSpec_NameAtVersion()
    {
        Assert.True(PackageSpecValidator.TryValidateSpec(
            "dsh-cost-meter@1.7.37", out var name, out var version, out var separator));
        Assert.Equal("dsh-cost-meter", name);
        Assert.Equal("1.7.37", version);
        Assert.Equal("@", separator);
    }

    [Fact]
    public void TryValidateSpec_ScopedNameAtVersion_UsesLastAtSign()
    {
        // 作用域包名字本身以 '@' 开头，版本分隔符必须取最后一个 '@'
        Assert.True(PackageSpecValidator.TryValidateSpec(
            "@furongjun1999/dsh-memory@0.6.0", out var name, out var version, out var separator));
        Assert.Equal("@furongjun1999/dsh-memory", name);
        Assert.Equal("0.6.0", version);
        Assert.Equal("@", separator);
    }

    [Fact]
    public void TryValidateSpec_ScopedNameWithoutVersion()
    {
        Assert.True(PackageSpecValidator.TryValidateSpec(
            "@deepseek-ai/dsh", out var name, out var version, out var separator));
        Assert.Equal("@deepseek-ai/dsh", name);
        Assert.Equal("", version);
        Assert.Equal("", separator);
    }

    [Fact]
    public void TryValidateSpec_HashSeparatorIsAccepted()
    {
        Assert.True(PackageSpecValidator.TryValidateSpec(
            "dsh-cost-meter#1.7.37", out var name, out var version, out var separator));
        Assert.Equal("dsh-cost-meter", name);
        Assert.Equal("1.7.37", version);
        Assert.Equal("#", separator);
    }

    [Theory]
    [InlineData("dsh-cost-meter --registry=http://evil")]
    [InlineData("dsh-cost-meter@1.7.37 --registry=http://evil")]
    [InlineData("@furongjun1999/dsh-memory@0.6.0 --registry=http://evil")]
    [InlineData("dsh-a@1.0.0;calc")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidateSpec_RejectsInjection(string? spec)
        => Assert.False(PackageSpecValidator.TryValidateSpec(spec, out _, out _, out _));

    // ==================== TryValidateSpecList ====================

    [Fact]
    public void TryValidateSpecList_MultiplePackagesSpaceSeparated()
    {
        Assert.True(PackageSpecValidator.TryValidateSpecList(
            "dsh-a dsh-b@1.2.3 @scope/c", out var specs, out var invalid));
        Assert.Equal("", invalid);
        Assert.Equal(new List<string> { "dsh-a", "dsh-b@1.2.3", "@scope/c" }, specs);
    }

    [Fact]
    public void TryValidateSpecList_CollapsesExtraWhitespace()
    {
        Assert.True(PackageSpecValidator.TryValidateSpecList(
            "  dsh-a \t dsh-b \r\n ", out var specs, out _));
        Assert.Equal(new List<string> { "dsh-a", "dsh-b" }, specs);
    }

    [Fact]
    public void TryValidateSpecList_RejectsWholeListWhenOneTokenIsBad()
    {
        // 部分安装比不安装更糟：用户会以为全装上了
        Assert.False(PackageSpecValidator.TryValidateSpecList(
            "dsh-a --registry=http://evil dsh-b", out var specs, out var invalid));
        Assert.Empty(specs);
        Assert.Equal("--registry=http://evil", invalid);
    }

    [Fact]
    public void TryValidateSpecList_RejectsEmptyInput()
    {
        Assert.False(PackageSpecValidator.TryValidateSpecList("   ", out var specs, out _));
        Assert.Empty(specs);
    }
}
