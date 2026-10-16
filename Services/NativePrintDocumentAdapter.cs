using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace SpeedEmulator.Services;

/// <summary>在原生文档排版前绑定文本单元格，保留表格样式，不覆盖 PDF。</summary>
internal static class NativePrintDocumentAdapter
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    internal static object Prepare(object document, Action<object> bindCells)
    {
        // 在原生组件生成后、PDF 排版前绑定数据，不修改 PDF 字节或覆盖页面。
        var contract = document.GetType().GetInterfaces().Single(type => type.FullName == "QuestPDF.Infrastructure.IDocument");
        var compose = contract.GetMethod("Compose")!;
        var containerType = compose.GetParameters()[0].ParameterType;
        var documentType = contract.Assembly.GetType("QuestPDF.Fluent.Document", throwOnError: true)!;
        Action<object> handler = container =>
        {
            compose.Invoke(document, [container]);
            bindCells(container);
        };
        var parameter = Expression.Parameter(containerType, "container");
        var callback = Expression.Lambda(typeof(Action<>).MakeGenericType(containerType),
            Expression.Invoke(Expression.Constant(handler), Expression.Convert(parameter, typeof(object))), parameter).Compile();
        var result = documentType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [callback])!;
        foreach (var name in new[] { "Metadata", "Settings" })
        {
            var value = contract.GetMethod("Get" + name)!.Invoke(document, null);
            documentType.GetMethod("With" + name)!.Invoke(result, [value]);
        }
        return result;
    }

    internal static object? Read(object target, string name) => target.GetType().GetProperty(name, Instance)?.GetValue(target);
    internal static int Number(object target, string name) => Convert.ToInt32(Read(target, name), CultureInfo.InvariantCulture);

    internal static IEnumerable<object> Descendants(object? root, Dictionary<Type, MethodInfo?> childMethods)
    {
        if (root is null) yield break;
        var stack = new Stack<object>();
        stack.Push(root);
        while (stack.TryPop(out var node))
        {
            yield return node;
            var type = node.GetType();
            if (!childMethods.TryGetValue(type, out var method))
                childMethods[type] = method = type.GetMethod("GetChildren", Instance);
            if (method?.Invoke(node, null) is IEnumerable children)
                foreach (var child in children.Cast<object?>().Where(child => child is not null).Reverse()) stack.Push(child!);
        }
    }

    internal static string ReadText(object cell, Dictionary<Type, MethodInfo?> childMethods) => string.Concat(
        Descendants(cell, childMethods).Where(node => node.GetType().FullName == "QuestPDF.Elements.Text.TextBlock")
            .SelectMany(node => (Read(node, "Items") as IEnumerable)?.Cast<object>() ?? [])
            .Select(item => Read(item, "Text") as string ?? ""));

    internal static void WriteText(object cell, string value, Dictionary<Type, MethodInfo?> childMethods)
    {
        var blocks = Descendants(cell, childMethods).Where(node => node.GetType().FullName == "QuestPDF.Elements.Text.TextBlock").ToArray();
        if (blocks.Length == 0)
        {
            if (value.Length == 0) return;
            // 原生代码遇到 null 时可能完全不创建 TextBlock；在最内层空容器
            // 创建文本，保留外侧边框、内边距和对齐方式，不覆盖整格样式。
            var leaf = Descendants(cell, childMethods).LastOrDefault(node =>
                node.GetType().GetProperty("Child", Instance)?.CanWrite == true
                && (Read(node, "Child") is null || Read(node, "Child")?.GetType().Name == "Empty"));
            var text = cell.GetType().Assembly.GetType("QuestPDF.Fluent.TextExtensions")?.GetMethods()
                .FirstOrDefault(method => method.Name == "Text" && method.GetParameters() is var parameters
                    && parameters.Length == 2 && parameters[1].ParameterType == typeof(string)
                    && parameters[0].ParameterType.FullName == "QuestPDF.Infrastructure.IContainer");
            if (leaf is null || text is null) throw new InvalidOperationException("原生模板数据单元格缺少可绑定的文本容器。");
            text.Invoke(null, [leaf, value]);
            return;
        }
        for (var index = 0; index < blocks.Length; index++)
        {
            var items = (IList)Read(blocks[index], "Items")!;
            var span = items.Cast<object>().FirstOrDefault(item => item.GetType().GetProperty("Text", Instance)?.CanWrite == true)
                ?? Activator.CreateInstance(blocks[index].GetType().Assembly.GetType("QuestPDF.Elements.Text.Items.TextBlockSpan", true)!, nonPublic: true)!;
            span.GetType().GetProperty("Text", Instance)!.SetValue(span, index == 0 ? value : "");
            items.Clear();
            items.Add(span);
        }
    }
}
