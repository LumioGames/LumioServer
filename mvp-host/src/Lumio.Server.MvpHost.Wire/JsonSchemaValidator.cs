using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lumio.Server.MvpHost.Wire
{
    /// <summary>
    /// 手写的 JSON Schema 子集校验器，只为镜像 schema 服务。
    ///
    /// **支持的构造是白名单，遇到白名单外的构造直接抛
    /// <see cref="SchemaConstructNotSupportedException"/>，绝不静默跳过。**
    /// 静默跳过一条不认识的约束＝本仓单方面放宽了一条公共契约，而且没人会发现；
    /// 抛出则会在第一次 sync 到新构造时立刻变红，逼人来读。
    ///
    /// 白名单逐条对着镜像 schema 的实测关键字统计定：
    /// <c>type / $ref / properties / required / additionalProperties / patternProperties /
    /// enum / const / pattern / minimum / maximum / minLength / maxLength /
    /// allOf / oneOf / if / then / items / uniqueItems</c>。
    ///
    /// <c>if</c> / <c>then</c> **必须支持**：ADR-045 之后 <c>replication-envelope.schema.json</c>
    /// 用 9 条 <c>allOf</c> 的 <c>if</c>/<c>then</c> 表达 body 封闭性，不支持就等于整个 body 面失守。
    /// （卡面原先要求「遇 if/then 抛出」，那是 ADR-045 之前的口径，已由总调度裁决改正。）
    /// </summary>
    internal static class JsonSchemaValidator
    {
        private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
        {
            "$schema", "$id", "$defs", "$comment", "title", "description", "examples", "default",
        };

        private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
        {
            "type", "$ref", "properties", "required", "additionalProperties", "patternProperties",
            "enum", "const", "pattern", "minimum", "maximum", "minLength", "maxLength",
            "allOf", "oneOf", "if", "then", "else", "items", "uniqueItems",
        };

        /// <summary>校验通过返回 null，否则返回**首条**失败的人类可读说明（含 JSON 路径）。</summary>
        internal static string? Validate(JsonNode? instance, JsonNode schema, string documentId, string path = "$")
        {
            switch (schema)
            {
                case JsonValue boolean when boolean.TryGetValue(out bool allowed):
                    // 布尔 schema：true 恒过、false 恒失败。
                    return allowed ? null : $"{path}: schema 为 false，任何值都不接受";
                case JsonObject obj:
                    return ValidateObjectSchema(instance, obj, documentId, path);
                default:
                    throw new SchemaConstructNotSupportedException($"{path}: schema 既不是对象也不是布尔");
            }
        }

        private static string? ValidateObjectSchema(JsonNode? instance, JsonObject schema, string documentId, string path)
        {
            foreach (var keyword in schema.Select(p => p.Key))
            {
                if (!Supported.Contains(keyword) && !Ignored.Contains(keyword))
                {
                    throw new SchemaConstructNotSupportedException(
                        $"{path}: 不支持的 schema 构造 '{keyword}'——校验器必须先学会它，不能装作没看见");
                }
            }

            if (schema.TryGetPropertyValue("$ref", out var refNode) && refNode is not null)
            {
                var target = MirroredSchemas.Resolve(refNode.GetValue<string>(), documentId);
                var targetDocument = RefDocumentOf(refNode.GetValue<string>(), documentId);
                var refFailure = Validate(instance, target, targetDocument, path);
                if (refFailure is not null)
                {
                    return refFailure;
                }
            }

            if (schema.TryGetPropertyValue("type", out var typeNode) && typeNode is not null)
            {
                var failure = ValidateType(instance, typeNode.GetValue<string>(), path);
                if (failure is not null)
                {
                    return failure;
                }
            }

            if (schema.TryGetPropertyValue("const", out var constNode))
            {
                if (!JsonNode.DeepEquals(instance, constNode))
                {
                    return $"{path}: 期望常量 {constNode?.ToJsonString()}，实际 {instance?.ToJsonString() ?? "null"}";
                }
            }

            if (schema.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray allowedValues)
            {
                if (!allowedValues.Any(v => JsonNode.DeepEquals(instance, v)))
                {
                    return $"{path}: {instance?.ToJsonString() ?? "null"} 不在 enum {enumNode.ToJsonString()} 内";
                }
            }

            var stringFailure = ValidateStringFacets(instance, schema, path);
            if (stringFailure is not null)
            {
                return stringFailure;
            }

            var numberFailure = ValidateNumberFacets(instance, schema, path);
            if (numberFailure is not null)
            {
                return numberFailure;
            }

            var objectFailure = ValidateObjectFacets(instance, schema, documentId, path);
            if (objectFailure is not null)
            {
                return objectFailure;
            }

            var arrayFailure = ValidateArrayFacets(instance, schema, documentId, path);
            if (arrayFailure is not null)
            {
                return arrayFailure;
            }

            return ValidateCombinators(instance, schema, documentId, path);
        }

        private static string RefDocumentOf(string reference, string currentDocumentId)
        {
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            return hash <= 0 ? currentDocumentId : reference[..hash];
        }

        private static string? ValidateType(JsonNode? instance, string type, string path)
        {
            var ok = type switch
            {
                "object" => instance is JsonObject,
                "array" => instance is JsonArray,
                "string" => instance is JsonValue s && s.TryGetValue<string>(out _),
                "boolean" => instance is JsonValue b && b.TryGetValue<bool>(out _),
                "integer" => IsInteger(instance),
                "number" => instance is JsonValue n && n.TryGetValue<double>(out _),
                "null" => instance is null,
                _ => throw new SchemaConstructNotSupportedException($"{path}: 不支持的 type '{type}'"),
            };

            return ok ? null : $"{path}: 期望 type={type}，实际 {Describe(instance)}";
        }

        private static string? ValidateStringFacets(JsonNode? instance, JsonObject schema, string path)
        {
            if (instance is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                return null;
            }

            if (schema.TryGetPropertyValue("pattern", out var patternNode) && patternNode is not null)
            {
                var pattern = patternNode.GetValue<string>();
                if (!Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                {
                    return $"{path}: \"{text}\" 不匹配 pattern {pattern}";
                }
            }

            if (schema.TryGetPropertyValue("minLength", out var minLength) && minLength is not null
                && text.Length < minLength.GetValue<int>())
            {
                return $"{path}: 长度 {text.Length} < minLength {minLength.GetValue<int>()}";
            }

            if (schema.TryGetPropertyValue("maxLength", out var maxLength) && maxLength is not null
                && text.Length > maxLength.GetValue<int>())
            {
                return $"{path}: 长度 {text.Length} > maxLength {maxLength.GetValue<int>()}";
            }

            return null;
        }

        private static string? ValidateNumberFacets(JsonNode? instance, JsonObject schema, string path)
        {
            if (instance is not JsonValue value || !TryGetDecimal(value, out var number))
            {
                return null;
            }

            if (schema.TryGetPropertyValue("minimum", out var minimum) && minimum is not null
                && number < minimum.GetValue<decimal>())
            {
                return $"{path}: {number} < minimum {minimum.GetValue<decimal>()}";
            }

            if (schema.TryGetPropertyValue("maximum", out var maximum) && maximum is not null
                && number > maximum.GetValue<decimal>())
            {
                return $"{path}: {number} > maximum {maximum.GetValue<decimal>()}";
            }

            return null;
        }

        private static string? ValidateObjectFacets(JsonNode? instance, JsonObject schema, string documentId, string path)
        {
            if (instance is not JsonObject value)
            {
                return null;
            }

            if (schema.TryGetPropertyValue("required", out var requiredNode) && requiredNode is JsonArray required)
            {
                foreach (var item in required)
                {
                    var name = item!.GetValue<string>();
                    if (!value.ContainsKey(name))
                    {
                        return $"{path}: 缺必填成员 '{name}'";
                    }
                }
            }

            var properties = schema.TryGetPropertyValue("properties", out var p) ? p as JsonObject : null;
            var patternProperties = schema.TryGetPropertyValue("patternProperties", out var pp) ? pp as JsonObject : null;

            if (properties is not null)
            {
                foreach (var (name, subSchema) in properties)
                {
                    if (value.TryGetPropertyValue(name, out var child) && subSchema is not null)
                    {
                        var failure = Validate(child, subSchema, documentId, $"{path}.{name}");
                        if (failure is not null)
                        {
                            return failure;
                        }
                    }
                }
            }

            if (patternProperties is not null)
            {
                foreach (var (name, child) in value)
                {
                    foreach (var (pattern, subSchema) in patternProperties)
                    {
                        if (Regex.IsMatch(name, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)) && subSchema is not null)
                        {
                            var failure = Validate(child, subSchema, documentId, $"{path}.{name}");
                            if (failure is not null)
                            {
                                return failure;
                            }
                        }
                    }
                }
            }

            if (schema.TryGetPropertyValue("additionalProperties", out var additional) && additional is not null)
            {
                var declared = new HashSet<string>(properties?.Select(x => x.Key) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

                foreach (var (name, child) in value)
                {
                    if (declared.Contains(name))
                    {
                        continue;
                    }

                    if (patternProperties is not null
                        && patternProperties.Any(x => Regex.IsMatch(name, x.Key, RegexOptions.None, TimeSpan.FromSeconds(1))))
                    {
                        continue;
                    }

                    if (additional is JsonValue flag && flag.TryGetValue<bool>(out var allowed))
                    {
                        if (!allowed)
                        {
                            return $"{path}: 不允许的额外成员 '{name}'";
                        }

                        continue;
                    }

                    var failure = Validate(child, additional, documentId, $"{path}.{name}");
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }

            return null;
        }

        private static string? ValidateArrayFacets(JsonNode? instance, JsonObject schema, string documentId, string path)
        {
            if (instance is not JsonArray value)
            {
                return null;
            }

            if (schema.TryGetPropertyValue("items", out var items) && items is not null)
            {
                for (var i = 0; i < value.Count; i++)
                {
                    var failure = Validate(value[i], items, documentId, $"{path}[{i}]");
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }

            if (schema.TryGetPropertyValue("uniqueItems", out var unique) && unique is not null
                && unique.GetValue<bool>())
            {
                for (var i = 0; i < value.Count; i++)
                {
                    for (var j = i + 1; j < value.Count; j++)
                    {
                        if (JsonNode.DeepEquals(value[i], value[j]))
                        {
                            return $"{path}: uniqueItems 违例，第 {i} 与第 {j} 项相等";
                        }
                    }
                }
            }

            return null;
        }

        private static string? ValidateCombinators(JsonNode? instance, JsonObject schema, string documentId, string path)
        {
            if (schema.TryGetPropertyValue("allOf", out var allOfNode) && allOfNode is JsonArray allOf)
            {
                foreach (var branch in allOf)
                {
                    var failure = Validate(instance, branch!, documentId, path);
                    if (failure is not null)
                    {
                        return failure;
                    }
                }
            }

            if (schema.TryGetPropertyValue("oneOf", out var oneOfNode) && oneOfNode is JsonArray oneOf)
            {
                var matched = oneOf.Count(branch => Validate(instance, branch!, documentId, path) is null);
                if (matched != 1)
                {
                    return $"{path}: oneOf 恰需 1 个分支成立，实际 {matched} 个";
                }
            }

            // if/then：条件分支不成立时**整条约束不适用**，不是失败。
            // envelope 的 9 条 allOf 全是这个形状——每个 messageType 一条，
            // 条件是 messageType 的 const，then 里是该类型的 body 封闭定义。
            if (schema.TryGetPropertyValue("if", out var ifNode) && ifNode is not null)
            {
                var conditionHolds = Validate(instance, ifNode, documentId, path) is null;

                if (conditionHolds && schema.TryGetPropertyValue("then", out var thenNode) && thenNode is not null)
                {
                    return Validate(instance, thenNode, documentId, path);
                }

                if (!conditionHolds && schema.TryGetPropertyValue("else", out var elseNode) && elseNode is not null)
                {
                    return Validate(instance, elseNode, documentId, path);
                }
            }

            return null;
        }

        private static bool IsInteger(JsonNode? instance)
        {
            if (instance is not JsonValue value)
            {
                return false;
            }

            if (value.TryGetValue<long>(out _) || value.TryGetValue<ulong>(out _))
            {
                return true;
            }

            // JSON 里 42.0 也是整数值；但 42.5 不是。
            return value.TryGetValue<decimal>(out var d) && d == decimal.Truncate(d);
        }

        private static bool TryGetDecimal(JsonValue value, out decimal number)
        {
            if (value.TryGetValue(out number))
            {
                return true;
            }

            if (value.TryGetValue<double>(out var d))
            {
                number = (decimal)d;
                return true;
            }

            number = default;
            return false;
        }

        private static string Describe(JsonNode? node) => node switch
        {
            null => "null",
            JsonObject => "object",
            JsonArray => "array",
            JsonValue v when v.TryGetValue<string>(out _) => "string",
            JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
            JsonValue v when v.TryGetValue<decimal>(out var d) => d == decimal.Truncate(d) ? "integer" : "number",
            _ => node.GetValueKind().ToString().ToLower(CultureInfo.InvariantCulture),
        };
    }
}
