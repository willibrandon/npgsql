using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Npgsql.Internal.Converters;
using Npgsql.Internal.Postgres;

namespace Npgsql.Internal.Resolvers;

[RequiresUnreferencedCode("Json serializer may perform reflection on trimmed types.")]
[RequiresDynamicCode("Serializing arbitary types to json can require creating new generic types or methods, which requires creating code at runtime. This may not work when AOT compiling.")]
class SystemTextJsonPocoTypeInfoResolver : DynamicTypeInfoResolver, IPgTypeInfoResolver
{
    protected TypeInfoMappingCollection Mappings { get; } = new();
    protected JsonSerializerOptions _serializerOptions;

    public SystemTextJsonPocoTypeInfoResolver(Type[]? jsonbClrTypes = null, Type[]? jsonClrTypes = null, JsonSerializerOptions? serializerOptions = null)
    {
#if NET7_0_OR_GREATER
        _serializerOptions = serializerOptions ??= JsonSerializerOptions.Default;
#else
        _serializerOptions = serializerOptions ??= new JsonSerializerOptions();
#endif

        AddMappings(Mappings, jsonbClrTypes ?? Array.Empty<Type>(), jsonClrTypes ?? Array.Empty<Type>(), serializerOptions);
    }

    void AddMappings(TypeInfoMappingCollection mappings, Type[] jsonbClrTypes, Type[] jsonClrTypes, JsonSerializerOptions serializerOptions)
    {
        // We do GetTypeInfo calls directly so we need a resolver.
        serializerOptions.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();

        AddUserMappings(jsonb: true, jsonbClrTypes);
        AddUserMappings(jsonb: false, jsonClrTypes);

        void AddUserMappings(bool jsonb, Type[] clrTypes)
        {
            var dynamicMappings = CreateCollection();
            var dataTypeName = (string)(jsonb ? DataTypeNames.Jsonb : DataTypeNames.Json);
            foreach (var jsonType in clrTypes)
            {
                var jsonTypeInfo = serializerOptions.GetTypeInfo(jsonType);
                dynamicMappings.AddMapping(jsonTypeInfo.Type, dataTypeName,
                    factory: (options, mapping, _) => mapping.CreateInfo(options,
                        CreateSystemTextJsonConverter(mapping.Type, jsonb, options.TextEncoding, serializerOptions, jsonType)));

                if (!jsonType.IsValueType && jsonTypeInfo.PolymorphismOptions is not null)
                {
                    foreach (var derived in jsonTypeInfo.PolymorphismOptions.DerivedTypes)
                        dynamicMappings.AddMapping(derived.DerivedType, dataTypeName,
                            factory: (options, mapping, _) => mapping.CreateInfo(options,
                                CreateSystemTextJsonConverter(mapping.Type, jsonb, options.TextEncoding, serializerOptions, jsonType)));
                }
            }
            mappings.AddRange(dynamicMappings.ToTypeInfoMappingCollection());
        }
    }

    protected void AddArrayInfos(TypeInfoMappingCollection mappings, TypeInfoMappingCollection baseMappings)
    {
        if (baseMappings.Items.Count == 0)
            return;

        var dynamicMappings = CreateCollection(baseMappings);
        foreach (var mapping in baseMappings.Items)
            dynamicMappings.AddArrayMapping(mapping.Type, mapping.DataTypeName);
        mappings.AddRange(dynamicMappings.ToTypeInfoMappingCollection());
    }

    public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
        => Mappings.Find(type, dataTypeName, options) ?? base.GetTypeInfo(type, dataTypeName, options);

    protected override DynamicMappingCollection? GetMappings(Type? type, DataTypeName dataTypeName, PgSerializerOptions options)
    {
        // Match all types except null, object and text types as long as DataTypeName (json/jsonb) is present.
        if (type is null || type == typeof(object) || Array.IndexOf(PgSerializerOptions.WellKnownTextTypes, type) != -1
                                   || dataTypeName != DataTypeNames.Jsonb && dataTypeName != DataTypeNames.Json)
            return null;

        return CreateCollection().AddMapping(type, dataTypeName, (options, mapping, _) =>
        {
            var jsonb = dataTypeName == DataTypeNames.Jsonb;

            // For jsonb we can't properly support polymorphic serialization unless we do quite some additional work
            // so we default to mapping.Type instead (exact types will never serialize their "$type" fields, essentially disabling the feature).
            var baseType = jsonb ? mapping.Type : typeof(object);

            return mapping.CreateInfo(options,
                CreateSystemTextJsonConverter(mapping.Type, jsonb, options.TextEncoding, _serializerOptions, baseType));
        });
    }

    static PgConverter CreateSystemTextJsonConverter(Type valueType, bool jsonb, Encoding textEncoding, JsonSerializerOptions serializerOptions, Type baseType)
        => (PgConverter)Activator.CreateInstance(
                typeof(SystemTextJsonConverter<,>).MakeGenericType(valueType, baseType),
                jsonb,
                textEncoding,
                serializerOptions
            )!;
}

[RequiresUnreferencedCode("Json serializer may perform reflection on trimmed types.")]
[RequiresDynamicCode("Serializing arbitary types to json can require creating new generic types or methods, which requires creating code at runtime. This may not work when AOT compiling.")]
sealed class SystemTextJsonPocoArrayTypeInfoResolver : SystemTextJsonPocoTypeInfoResolver, IPgTypeInfoResolver
{
    new TypeInfoMappingCollection Mappings { get; }

    public SystemTextJsonPocoArrayTypeInfoResolver(Type[]? jsonbClrTypes = null, Type[]? jsonClrTypes = null, JsonSerializerOptions? serializerOptions = null)
        : base(jsonbClrTypes, jsonClrTypes, serializerOptions)
    {
        Mappings = new TypeInfoMappingCollection(base.Mappings);
        AddArrayInfos(Mappings, base.Mappings);
    }

    public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
        => Mappings.Find(type, dataTypeName, options) ?? base.GetTypeInfo(type, dataTypeName, options);

    protected override DynamicMappingCollection? GetMappings(Type? type, DataTypeName dataTypeName, PgSerializerOptions options)
        => type is not null && IsArrayLikeType(type, out var elementType) && IsArrayDataTypeName(dataTypeName, options, out var elementDataTypeName)
            ? base.GetMappings(elementType, elementDataTypeName, options)?.AddArrayMapping(elementType, elementDataTypeName)
            : null;
}
