// ============================================================================
// net48 兼容垫片（polyfill）
//
// C# 9 的 `init` 访问器与 C# 11 的 `required` 成员在底层依赖 .NET 运行时中的
// 几个 BCL 类型，而这些类型在 .NET Framework 4.8 引用程序集中并不存在
// （.NET 5+/net8 才有）。本工程已改以 net48 为目标（为在 Windows 7 上运行），
// 但源码里仍用了 `init` / `required`（见 FirebirdHeader.cs、EnginePinning.cs、
// Program.cs 的 record 类型），编译器会报 CS0518 / CS0656。
//
// 这些类型只在**编译期**被编译器识别并织入属性/特性，运行时行为（init 的
// 一次性赋值、required 的构造期必填检查）由编译器保证，因此这里给出空实现的
// internal 类即可，无需任何运行时代码。
// ============================================================================
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// 标记 <c>init</c> 访问器所需的占位类型（注意：这是一个普通标记类型，**不是**
    /// 特性，故不可加 <c>[AttributeUsage]</c>）。编译器用它来判断一个属性是否允许
    /// 在对象初始化器中被赋值。
    /// </summary>
    internal sealed class IsExternalInit
    {
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// 标记被 <c>required</c> 修饰的成员（属性/字段），供编译器做必填检查。
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// 标记某个成员依赖某个编译器特性（如 <c>RequiredMembers</c> / <c>RefStructs</c>）。
    /// 由 <c>required</c> 成员自动织入。
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event,
        AllowMultiple = false,
        Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
        {
            FeatureName = featureName;
        }

        public string FeatureName { get; }

        public bool IsOptional => false;

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// 标记某个构造函数会初始化所有 <c>required</c> 成员，调用方可跳过必填检查。
    /// </summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
