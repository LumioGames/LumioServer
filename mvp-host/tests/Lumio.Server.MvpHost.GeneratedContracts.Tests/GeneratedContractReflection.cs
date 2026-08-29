using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Lumio.Server.MvpHost.GeneratedContracts.Tests
{
    /// <summary>
    /// 对 <c>Lumio.Server.MvpHost.GeneratedContracts</c> **这一个程序集**内、
    /// <c>Lumio.Gen.*</c> 命名空间下的反射入口。
    ///
    /// 范围**刻意写死为单个程序集名**，不得改成「遍历本工程引用到的全部
    /// <c>Lumio.Gen.*</c> 程序集」：契约生成物走源码拷贝，架构源的
    /// <c>Lumio.Gen.*</c> 从来不是独立程序集，那样写集合恒空、断言静默失效
    /// （设计 §4 第 4 条、§5.3 护栏 5）。
    ///
    /// 用 <see cref="Assembly.Load(string)"/> 按名加载而非 <c>typeof(某类型).Assembly</c>：
    /// 后者是编译期耦合，会让「类型尚未拷进来」这一状态表现为**编译失败**而不是**断言变红**，
    /// 自过期守卫的红/绿信号因此丢失。
    /// </summary>
    internal static class GeneratedContractReflection
    {
        internal const string AssemblyName = "Lumio.Server.MvpHost.GeneratedContracts";

        internal static Assembly Assembly { get; } = Assembly.Load(AssemblyName);

        internal static IReadOnlyList<Type> PublicGenTypes { get; } = Assembly
            .GetExportedTypes()
            .Where(t => t.Namespace is not null && t.Namespace.StartsWith("Lumio.Gen.", StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        internal static Type Require(string fullName)
        {
            var type = PublicGenTypes.SingleOrDefault(t => t.FullName == fullName);
            if (type is null)
            {
                throw new InvalidOperationException(
                    $"{AssemblyName} 内找不到公开类型 {fullName}；已见 {PublicGenTypes.Count} 个 Lumio.Gen.* 公开类型。");
            }

            return type;
        }

        internal static T StaticValue<T>(string typeFullName, string memberName)
        {
            var type = Require(typeFullName);

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
            if (field is not null)
            {
                return (T)field.GetValue(null)!;
            }

            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
            if (property is not null)
            {
                return (T)property.GetValue(null)!;
            }

            throw new InvalidOperationException($"{typeFullName} 上没有公开静态成员 {memberName}。");
        }
    }
}
