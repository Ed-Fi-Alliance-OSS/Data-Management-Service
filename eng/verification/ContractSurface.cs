// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Reads the externally consumable surface of an assembly out of its metadata and writes it as
// sorted, normalized lines.
//
// Loaded by eng/verification/Get-ContractPublicSurface.ps1 with Add-Type -Path. It is a source file
// rather than a here-string so that CSharpier formats it and a reader can diff it.
//
// Metadata only, through PEReader and MetadataReader: nothing here loads an assembly into an
// execution context. That is what lets the publish comparison read a package downloaded from the
// feed without resolving its references, and it is what keeps a candidate assembly's code from ever
// running in the process that is inspecting it.
//
// What is emitted is what an implementer outside this assembly can bind to, or is bound by.
// private protected (FamANDAssem) members are never described: a consumer cannot call, override or
// name one, so its signature is not part of the contract. One fact about them is. An abstract
// private protected member cannot be satisfied by any type outside the assembly, so its presence
// closes the hierarchy to every external deriver (CustomValidationFailure.EnsureClosed exists for
// exactly that), and adding or removing it changes what an implementer may derive from. That fact
// is rendered as one CLOSURE line per type, without the member's name or shape, so a rename or a
// changed parameter list on the closing member at an unchanged version is the non-change it is to
// every consumer. Widening a private protected member to protected or public, or narrowing one the
// other way, was already visible: the member's line appears or disappears.
//
// Nullability is part of the surface. The effective nullable annotation of every described
// return, parameter, property, indexer parameter, field and event type is rendered as a vector
// with one entry per position the compiler annotates, computed from NullableAttribute where the
// compiler emitted one and from the effective NullableContextAttribute where it did not, so that
// the same source compiled with the attribute placed differently renders identically. The
// nullable-flow attributes from System.Diagnostics.CodeAnalysis (AllowNull, DisallowNull,
// MaybeNull, NotNull, MaybeNullWhen, NotNullWhen, NotNullIfNotNull, DoesNotReturn,
// DoesNotReturnIf) are rendered on the rows the compiler places them on. Outside this comparison,
// deliberately: the nullability of a type's base type and interfaces, MemberNotNull and
// MemberNotNullWhen, and every other attribute, such as [Obsolete]. This is a publish gate for two
// specific contracts, not a general API-compatibility analyzer.

// Add-Type compiles this file on its own rather than inside a project, so the nullable context the
// repository's csproj files set through <Nullable>enable</Nullable> has to be declared here.
#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace EdFi.Verification;

/// <summary>
/// The generic parameter names in scope while one signature is decoded, so that a decoded signature
/// reads <c>TValue</c> rather than <c>!0</c> and a renamed type parameter is a visible change.
/// </summary>
public sealed class GenericContext(
    ImmutableArray<string> typeParameters,
    ImmutableArray<string> methodParameters,
    ImmutableArray<bool> typeParameterIsValueType,
    ImmutableArray<bool> methodParameterIsValueType
)
{
    public GenericContext(ImmutableArray<string> typeParameters, ImmutableArray<string> methodParameters)
        : this(
            typeParameters,
            methodParameters,
            ImmutableArray.CreateRange(typeParameters.Select(_ => false)),
            ImmutableArray.CreateRange(methodParameters.Select(_ => false))
        ) { }

    public ImmutableArray<string> TypeParameters { get; } = typeParameters;

    public ImmutableArray<string> MethodParameters { get; } = methodParameters;

    /// <summary>
    /// Whether each type parameter carries the struct constraint. The compiler records a
    /// struct-constrained parameter as a fixed-zero nullability position rather than an annotatable
    /// one, so the shape provider needs the constraint, not only the name.
    /// </summary>
    public ImmutableArray<bool> TypeParameterIsValueType { get; } = typeParameterIsValueType;

    public ImmutableArray<bool> MethodParameterIsValueType { get; } = methodParameterIsValueType;

    public static GenericContext Empty { get; } =
        new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);
}

/// <summary>
/// Decodes signature blobs into stable type names.
/// </summary>
public sealed class ContractSignatureProvider(MetadataReader reader)
    : ISignatureTypeProvider<string, GenericContext>
{
    public string GetArrayType(string elementType, ArrayShape shape) =>
        elementType + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetFunctionPointerType(MethodSignature<string> signature) =>
        "delegate*<" + string.Join(",", signature.ParameterTypes) + "," + signature.ReturnType + ">";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";

    public string GetGenericMethodParameter(GenericContext context, int index) =>
        index < context.MethodParameters.Length ? context.MethodParameters[index] : "!!" + index;

    public string GetGenericTypeParameter(GenericContext context, int index) =>
        index < context.TypeParameters.Length ? context.TypeParameters[index] : "!" + index;

    // Preserved rather than unwrapped. `in` parameters and `ref readonly` returns are expressed as a
    // required modifier on an ordinary byref, so dropping modifiers here would erase a distinction a
    // caller can see.
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
        (isRequired ? "modreq(" : "modopt(") + modifier + ") " + unmodifiedType;

    public string GetPinnedType(string elementType) => "pinned " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            _ => typeCode.ToString(),
        };

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(
        MetadataReader metadataReader,
        TypeDefinitionHandle handle,
        byte rawTypeKind
    ) => ContractSurfaceReader.FullName(metadataReader, handle);

    public string GetTypeFromReference(
        MetadataReader metadataReader,
        TypeReferenceHandle handle,
        byte rawTypeKind
    ) => ContractSurfaceReader.FullName(metadataReader, handle);

    public string GetTypeFromSpecification(
        MetadataReader metadataReader,
        GenericContext context,
        TypeSpecificationHandle handle,
        byte rawTypeKind
    ) => metadataReader.GetTypeSpecification(handle).DecodeSignature(this, context);

    public MetadataReader Reader { get; } = reader;
}

/// <summary>
/// The nullability positions of one decoded type, in the preorder the compiler's NullableAttribute
/// uses: one entry per position, true where the compiler records an annotation and false where it
/// records a fixed 0.
/// </summary>
/// <remarks>
/// The rules mirror what the compiler emits, checked against compiled probes. A reference type, an
/// array or an unconstrained generic parameter is one annotatable position; a struct-constrained
/// generic parameter is one fixed-zero position. A non-generic value type is no position at all: an
/// int parameter carries no NullableAttribute under any context. A generic value-type instantiation
/// is one fixed-zero position followed by its arguments, whatever they are (KeyValuePair&lt;string,
/// int&gt; is [0,1], and Dictionary&lt;(int, int), string?&gt;? is [2,0,2]); when nothing in it is
/// annotatable, as in a bare KeyValuePair&lt;int, int&gt;, the compiler writes no attribute and the
/// vector is all zeros under every context. System.Nullable&lt;T&gt; is transparent: int? is no
/// position and KeyValuePair&lt;string?, int&gt;? is [0,2]. By-ref, modified and pinned types are
/// transparent. Pointers and function pointers are a fixed-zero position followed by their element
/// or signature. Where a real assembly's explicit NullableAttribute array disagrees with a shape
/// computed here, the reader throws rather than guessing.
/// </remarks>
public sealed class NullabilityShape
{
    private NullabilityShape(ImmutableArray<bool> positions, bool isValueType, bool isSystemNullable)
    {
        Positions = positions;
        IsValueType = isValueType;
        IsSystemNullable = isSystemNullable;
    }

    public ImmutableArray<bool> Positions { get; }

    /// <summary>True for a bare value type definition or reference, which contributes no position.</summary>
    public bool IsValueType { get; }

    /// <summary>True for the bare System.Nullable`1 definition, whose instantiation is transparent.</summary>
    public bool IsSystemNullable { get; }

    public bool HasAnnotatablePosition => Positions.Contains(true);

    public static NullabilityShape None { get; } = new(ImmutableArray<bool>.Empty, false, false);

    public static NullabilityShape ValueType { get; } = new(ImmutableArray<bool>.Empty, true, false);

    public static NullabilityShape SystemNullable { get; } = new(ImmutableArray<bool>.Empty, true, true);

    public static NullabilityShape Annotatable { get; } = new([true], false, false);

    public static NullabilityShape FixedZero { get; } = new([false], false, false);

    public NullabilityShape Prepend(bool annotatable) => new([annotatable, .. Positions], false, false);

    public static NullabilityShape Concat(IEnumerable<NullabilityShape> shapes) =>
        new([.. shapes.SelectMany(shape => shape.Positions)], false, false);
}

/// <summary>
/// Decodes signature blobs into their nullability positions.
/// </summary>
public sealed class NullabilityShapeProvider : ISignatureTypeProvider<NullabilityShape, GenericContext>
{
    // ELEMENT_TYPE_VALUETYPE and ELEMENT_TYPE_CLASS, the two codes a signature uses to introduce a
    // type definition or reference. A signature never carries any other, so any other is corrupt.
    private const byte ElementTypeValueType = 0x11;
    private const byte ElementTypeClass = 0x12;

    public NullabilityShape GetArrayType(NullabilityShape elementType, ArrayShape shape) =>
        elementType.Prepend(true);

    public NullabilityShape GetByReferenceType(NullabilityShape elementType) => elementType;

    public NullabilityShape GetFunctionPointerType(MethodSignature<NullabilityShape> signature) =>
        NullabilityShape.Concat([signature.ReturnType, .. signature.ParameterTypes]).Prepend(false);

    public NullabilityShape GetGenericInstantiation(
        NullabilityShape genericType,
        ImmutableArray<NullabilityShape> typeArguments
    )
    {
        NullabilityShape arguments = NullabilityShape.Concat(typeArguments);

        if (genericType.IsSystemNullable)
        {
            return arguments;
        }

        // A generic value type occupies a fixed-zero position of its own, whatever its arguments
        // hold: Dictionary<(int, int), string?>? is [2,0,2], with the tuple's slot present even
        // though nothing inside it is annotatable. When the whole shape has no annotatable position
        // the compiler emits no attribute, which the vector computation renders as zeros.
        return arguments.Prepend(!genericType.IsValueType);
    }

    // A struct-constrained parameter is a value type to the compiler and gets a fixed 0; any other
    // generic parameter is annotatable. An index past the context's knowledge is treated as
    // annotatable, the same fallback the name provider takes for an unknown parameter.
    public NullabilityShape GetGenericMethodParameter(GenericContext context, int index) =>
        index < context.MethodParameterIsValueType.Length && context.MethodParameterIsValueType[index]
            ? NullabilityShape.FixedZero
            : NullabilityShape.Annotatable;

    public NullabilityShape GetGenericTypeParameter(GenericContext context, int index) =>
        index < context.TypeParameterIsValueType.Length && context.TypeParameterIsValueType[index]
            ? NullabilityShape.FixedZero
            : NullabilityShape.Annotatable;

    public NullabilityShape GetModifiedType(
        NullabilityShape modifier,
        NullabilityShape unmodifiedType,
        bool isRequired
    ) => unmodifiedType;

    public NullabilityShape GetPinnedType(NullabilityShape elementType) => elementType;

    public NullabilityShape GetPointerType(NullabilityShape elementType) => elementType.Prepend(false);

    public NullabilityShape GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        typeCode is PrimitiveTypeCode.String or PrimitiveTypeCode.Object
            ? NullabilityShape.Annotatable
            : NullabilityShape.ValueType;

    public NullabilityShape GetSZArrayType(NullabilityShape elementType) => elementType.Prepend(true);

    public NullabilityShape GetTypeFromDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        byte rawTypeKind
    )
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);

        return Classify(rawTypeKind, reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    public NullabilityShape GetTypeFromReference(
        MetadataReader reader,
        TypeReferenceHandle handle,
        byte rawTypeKind
    )
    {
        TypeReference type = reader.GetTypeReference(handle);

        return Classify(rawTypeKind, reader.GetString(type.Namespace), reader.GetString(type.Name));
    }

    public NullabilityShape GetTypeFromSpecification(
        MetadataReader reader,
        GenericContext context,
        TypeSpecificationHandle handle,
        byte rawTypeKind
    ) => reader.GetTypeSpecification(handle).DecodeSignature(this, context);

    private static NullabilityShape Classify(byte rawTypeKind, string typeNamespace, string typeName) =>
        rawTypeKind switch
        {
            ElementTypeClass => NullabilityShape.Annotatable,
            ElementTypeValueType => typeNamespace == "System" && typeName == "Nullable`1"
                ? NullabilityShape.SystemNullable
                : NullabilityShape.ValueType,
            _ => throw new BadImageFormatException(
                $"A signature introduces {typeNamespace}.{typeName} with element type 0x{rawTypeKind:X2}, which is neither CLASS nor VALUETYPE."
            ),
        };
}

/// <summary>
/// Emits one assembly's externally consumable surface.
/// </summary>
public static class ContractSurfaceReader
{
    private static readonly NullabilityShapeProvider ShapeProvider = new();

    // The nullable-flow attributes rendered on the rows the compiler places them on. Their argument
    // shapes are fixed (none, one bool, or one string), and anything else is refused.
    private static readonly ImmutableHashSet<string> FlowAttributeNames =
    [
        "AllowNullAttribute",
        "DisallowNullAttribute",
        "MaybeNullAttribute",
        "NotNullAttribute",
        "MaybeNullWhenAttribute",
        "NotNullWhenAttribute",
        "NotNullIfNotNullAttribute",
        "DoesNotReturnAttribute",
        "DoesNotReturnIfAttribute",
    ];

    /// <summary>
    /// Reads <paramref name="assemblyPath"/> and returns its surface as ordinally sorted lines.
    /// </summary>
    public static string[] Read(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("Assembly not found.", assemblyPath);
        }

        List<string> lines = [];

        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(stream);

        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException(
                $"{assemblyPath} carries no managed metadata, so it has no contract surface to read."
            );
        }

        MetadataReader reader = peReader.GetMetadataReader();
        ContractSignatureProvider provider = new(reader);

        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);

            if (!IsVisible(reader, handle))
            {
                continue;
            }

            string name = FullName(reader, handle);
            ImmutableArray<string> typeParameters = GenericParameterNames(
                reader,
                type.GetGenericParameters()
            );
            GenericContext typeContext = new(
                typeParameters,
                ImmutableArray<string>.Empty,
                GenericParameterValueTypeFlags(reader, type.GetGenericParameters()),
                ImmutableArray<bool>.Empty
            );

            lines.Add(DescribeType(reader, provider, handle, type, name, typeParameters, typeContext));

            foreach (string line in DescribeMembers(reader, provider, type, name, typeContext))
            {
                lines.Add(line);
            }
        }

        lines.Sort(StringComparer.Ordinal);

        return [.. lines];
    }

    private static string DescribeType(
        MetadataReader reader,
        ContractSignatureProvider provider,
        TypeDefinitionHandle handle,
        TypeDefinition type,
        string name,
        ImmutableArray<string> typeParameters,
        GenericContext context
    )
    {
        TypeAttributes attributes = type.Attributes;
        StringBuilder builder = new();

        builder.Append("TYPE ").Append(name);
        builder.Append(" accessibility=").Append(TypeAccessibility(attributes));
        builder.Append(" kind=").Append(TypeKind(reader, type, attributes));

        List<string> modifiers = [];
        if (attributes.HasFlag(TypeAttributes.Abstract))
        {
            modifiers.Add("abstract");
        }
        if (attributes.HasFlag(TypeAttributes.Sealed))
        {
            modifiers.Add("sealed");
        }
        builder.Append(" modifiers=").Append(modifiers.Count == 0 ? "none" : string.Join(",", modifiers));

        builder.Append(" base=").Append(TypeName(reader, provider, type.BaseType, context));

        // Ordered, so that a reordering of the declaration is not reported as a change while an
        // added or removed interface is.
        List<string> interfaces = [];
        foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
        {
            interfaces.Add(
                TypeName(
                    reader,
                    provider,
                    reader.GetInterfaceImplementation(implementation).Interface,
                    context
                )
            );
        }
        interfaces.Sort(StringComparer.Ordinal);
        builder.Append(" interfaces=").Append(interfaces.Count == 0 ? "none" : string.Join(",", interfaces));

        builder
            .Append(" generics=")
            .Append(DescribeGenericParameters(reader, provider, type.GetGenericParameters(), context));

        if (IsEnum(reader, type))
        {
            builder.Append(" underlying=").Append(EnumUnderlyingType(reader, provider, type, context));
        }

        return builder.ToString();
    }

    private static IEnumerable<string> DescribeMembers(
        MetadataReader reader,
        ContractSignatureProvider provider,
        TypeDefinition type,
        string typeName,
        GenericContext typeContext
    )
    {
        if (ClosesHierarchy(reader, type))
        {
            yield return "CLOSURE " + typeName + " by=private protected abstract member";
        }

        // Accessor methods are described through their property or event, where the member's own
        // name and any indexer parameters survive. get_Item alone does not name the indexer it
        // belongs to, and two indexers differing only in name would read identically.
        HashSet<int> accessors = [];

        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            PropertyAccessors propertyAccessors = reader.GetPropertyDefinition(handle).GetAccessors();
            AddAccessor(accessors, propertyAccessors.Getter);
            AddAccessor(accessors, propertyAccessors.Setter);
        }

        foreach (EventDefinitionHandle handle in type.GetEvents())
        {
            EventAccessors eventAccessors = reader.GetEventDefinition(handle).GetAccessors();
            AddAccessor(accessors, eventAccessors.Adder);
            AddAccessor(accessors, eventAccessors.Remover);
            AddAccessor(accessors, eventAccessors.Raiser);
        }

        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            if (accessors.Contains(MetadataTokens.GetRowNumber(handle)))
            {
                continue;
            }

            MethodDefinition method = reader.GetMethodDefinition(handle);

            if (!IsVisible(method.Attributes))
            {
                continue;
            }

            yield return DescribeMethod(reader, provider, handle, method, typeName, typeContext);
        }

        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(handle);

            if (!IsVisible(field.Attributes))
            {
                continue;
            }

            yield return DescribeField(reader, provider, field, typeName, typeContext);
        }

        foreach (PropertyDefinitionHandle handle in type.GetProperties())
        {
            if (TryDescribeProperty(reader, provider, handle, typeName, typeContext, out string line))
            {
                yield return line;
            }
        }

        foreach (EventDefinitionHandle handle in type.GetEvents())
        {
            if (TryDescribeEvent(reader, provider, handle, typeName, typeContext, out string line))
            {
                yield return line;
            }
        }
    }

    private static string DescribeMethod(
        MetadataReader reader,
        ContractSignatureProvider provider,
        MethodDefinitionHandle methodHandle,
        MethodDefinition method,
        string typeName,
        GenericContext typeContext
    )
    {
        ImmutableArray<string> methodParameters = GenericParameterNames(
            reader,
            method.GetGenericParameters()
        );
        GenericContext context = new(
            typeContext.TypeParameters,
            methodParameters,
            typeContext.TypeParameterIsValueType,
            GenericParameterValueTypeFlags(reader, method.GetGenericParameters())
        );
        MethodSignature<string> signature = method.DecodeSignature(provider, context);
        MethodSignature<NullabilityShape> shapes = method.DecodeSignature(ShapeProvider, context);
        string methodName = reader.GetString(method.Name);
        string owner = "method '" + typeName + "." + methodName + "'";

        List<string> rendered = DescribeSignatureParameters(
            reader,
            provider,
            signature.ParameterTypes,
            shapes.ParameterTypes,
            method.GetParameters(),
            methodHandle,
            owner
        );

        StringBuilder builder = new();
        builder.Append("METHOD ").Append(typeName).Append('.').Append(methodName);
        builder.Append('`').Append(signature.GenericParameterCount);
        builder.Append('(').Append(string.Join(", ", rendered)).Append(')');
        builder.Append(" : ").Append(signature.ReturnType);
        builder
            .Append(" nullable=")
            .Append(
                DescribeNullability(
                    reader,
                    provider,
                    ReturnParameterAttributes(reader, method.GetParameters()),
                    ContextChainForMethod(reader, methodHandle),
                    shapes.ReturnType,
                    "return of " + owner
                )
            );
        AppendFlowAttributes(
            builder,
            " nullattrs=",
            reader,
            provider,
            ReturnParameterAttributes(reader, method.GetParameters()),
            "return of " + owner
        );
        builder.Append(" accessibility=").Append(MemberAccessibility(method.Attributes));
        builder.Append(" modifiers=").Append(MethodModifiers(method.Attributes));
        builder
            .Append(" generics=")
            .Append(DescribeGenericParameters(reader, provider, method.GetGenericParameters(), context));
        AppendFlowAttributes(builder, " methodattrs=", reader, provider, method.GetCustomAttributes(), owner);

        return builder.ToString();
    }

    private static string DescribeParameter(
        MetadataReader reader,
        ContractSignatureProvider provider,
        string type,
        NullabilityShape shape,
        Parameter? parameter,
        IEnumerable<CustomAttributeHandleCollection> contextChain,
        string owner
    )
    {
        StringBuilder builder = new();

        if (parameter is Parameter value)
        {
            ParameterAttributes attributes = value.Attributes;

            // `out` is In=false/Out=true, `ref` is neither, and `in` arrives as a required modifier
            // on the byref type, which the signature provider has already preserved.
            if (attributes.HasFlag(ParameterAttributes.Out))
            {
                builder.Append("out ");
            }
            else if (attributes.HasFlag(ParameterAttributes.In))
            {
                builder.Append("in ");
            }

            if (HasAttribute(reader, value.GetCustomAttributes(), "System", "ParamArrayAttribute"))
            {
                builder.Append("params ");
            }

            builder.Append(type);

            // The name is part of what a caller compiles against: renaming a parameter breaks every
            // call site using a named argument, at an unchanged package version and with an
            // otherwise identical signature.
            string name = reader.GetString(value.Name);
            if (!string.IsNullOrEmpty(name))
            {
                builder.Append(' ').Append(name);
            }

            if (attributes.HasFlag(ParameterAttributes.Optional))
            {
                builder
                    .Append(" = ")
                    .Append(
                        DescribeDefaultValue(
                            reader,
                            provider,
                            value.GetDefaultValue(),
                            value.GetCustomAttributes(),
                            "parameter '" + name + "'"
                        )
                    );
            }

            string parameterOwner = "parameter '" + name + "' of " + owner;

            builder
                .Append(" nullable=")
                .Append(
                    DescribeNullability(
                        reader,
                        provider,
                        value.GetCustomAttributes(),
                        contextChain,
                        shape,
                        parameterOwner
                    )
                );
            AppendFlowAttributes(
                builder,
                " nullattrs=",
                reader,
                provider,
                value.GetCustomAttributes(),
                parameterOwner
            );

            return builder.ToString();
        }

        // A parameter with no metadata row carries no attribute, so its annotation is the context's.
        builder.Append(type);
        builder
            .Append(" nullable=")
            .Append(
                DescribeNullability(reader, provider, null, contextChain, shape, "parameter of " + owner)
            );

        return builder.ToString();
    }

    private static string DescribeField(
        MetadataReader reader,
        ContractSignatureProvider provider,
        FieldDefinition field,
        string typeName,
        GenericContext context
    )
    {
        FieldAttributes attributes = field.Attributes;
        StringBuilder builder = new();
        string fieldOwner = "field '" + typeName + "." + reader.GetString(field.Name) + "'";

        builder.Append("FIELD ").Append(typeName).Append('.').Append(reader.GetString(field.Name));
        builder.Append(" : ").Append(field.DecodeSignature(provider, context));
        builder
            .Append(" nullable=")
            .Append(
                DescribeNullability(
                    reader,
                    provider,
                    field.GetCustomAttributes(),
                    ContextChainForType(reader, field.GetDeclaringType()),
                    field.DecodeSignature(ShapeProvider, context),
                    fieldOwner
                )
            );
        AppendFlowAttributes(
            builder,
            " nullattrs=",
            reader,
            provider,
            field.GetCustomAttributes(),
            fieldOwner
        );
        builder.Append(" accessibility=").Append(FieldAccessibility(attributes));

        List<string> modifiers = [];
        if (attributes.HasFlag(FieldAttributes.Static))
        {
            modifiers.Add("static");
        }
        if (attributes.HasFlag(FieldAttributes.InitOnly))
        {
            modifiers.Add("readonly");
        }
        if (attributes.HasFlag(FieldAttributes.Literal))
        {
            modifiers.Add("const");
        }
        builder.Append(" modifiers=").Append(modifiers.Count == 0 ? "none" : string.Join(",", modifiers));

        // A public constant's value is compiled into every consumer, so changing it changes what
        // already-compiled code does without changing a single signature.
        //
        // Not gated on FieldAttributes.Literal. A `const decimal` is emitted as static readonly
        // carrying DecimalConstantAttribute, because the CLR has no decimal literal, so gating on
        // Literal would drop exactly the constants whose values are least visible elsewhere.
        string fieldName = reader.GetString(field.Name);
        string value = DescribeDefaultValue(
            reader,
            provider,
            field.GetDefaultValue(),
            field.GetCustomAttributes(),
            "field '" + typeName + "." + fieldName + "'"
        );

        if (attributes.HasFlag(FieldAttributes.Literal) || value != "none")
        {
            builder.Append(" value=").Append(value);
        }

        return builder.ToString();
    }

    private static bool TryDescribeProperty(
        MetadataReader reader,
        ContractSignatureProvider provider,
        PropertyDefinitionHandle handle,
        string typeName,
        GenericContext context,
        out string line
    )
    {
        line = string.Empty;

        PropertyDefinition property = reader.GetPropertyDefinition(handle);
        PropertyAccessors accessors = property.GetAccessors();

        string getter = AccessorAccessibility(reader, accessors.Getter);
        string setter = AccessorAccessibility(reader, accessors.Setter);

        if (getter == "none" && setter == "none")
        {
            return false;
        }

        MethodSignature<string> signature = property.DecodeSignature(provider, context);
        MethodSignature<NullabilityShape> shapes = property.DecodeSignature(ShapeProvider, context);
        string propertyName = reader.GetString(property.Name);
        string owner = "property '" + typeName + "." + propertyName + "'";

        // The accessor that exists, and both when both do. A property row has no parameter rows and
        // carries no NullableContext of its own, so names, indexer annotations and the context all
        // come from the accessors.
        MethodDefinitionHandle source = accessors.Getter.IsNil ? accessors.Setter : accessors.Getter;

        StringBuilder builder = new();
        builder.Append("PROPERTY ").Append(typeName).Append('.').Append(propertyName);

        if (signature.ParameterTypes.Length > 0)
        {
            // Indexer parameters carry names too, and a named argument binds to them exactly as it
            // does on a method. The names and annotations come from whichever accessor exists.
            builder
                .Append('[')
                .Append(
                    string.Join(
                        ", ",
                        DescribeSignatureParameters(
                            reader,
                            provider,
                            signature.ParameterTypes,
                            shapes.ParameterTypes,
                            reader.GetMethodDefinition(source).GetParameters(),
                            source,
                            owner
                        )
                    )
                )
                .Append(']');
        }

        builder.Append(" : ").Append(signature.ReturnType);

        // The property row's own annotation is governed by the declaring type's context, not by an
        // accessor's: NullableContextAttribute cannot target a property, and an accessor's context
        // describes that accessor's parameters and return. An indexer whose getter chose context 2
        // for its nullable index parameters still has a non-nullable property type under the type's
        // context 1, with the getter's return row carrying an explicit 1 of its own.
        builder
            .Append(" nullable=")
            .Append(
                DescribeNullability(
                    reader,
                    provider,
                    property.GetCustomAttributes(),
                    ContextChainForType(reader, reader.GetMethodDefinition(source).GetDeclaringType()),
                    shapes.ReturnType,
                    owner
                )
            );
        builder.Append(" get=").Append(getter);
        builder.Append(" set=").Append(setter);
        builder.Append(" setkind=").Append(SetterKind(reader, provider, accessors.Setter, context));

        // Flow annotations keep their roles. [NotNull] on the getter's return and [NotNull] on the
        // setter's value parameter are different promises, so the getter return row, the setter
        // value row and the property row are rendered as three labelled sets rather than one bag.
        // Only for accessors that are themselves part of the surface: an annotation on a private
        // accessor, which the line already reads as get=none or set=none, binds nobody outside.
        if (getter != "none")
        {
            AppendFlowAttributes(
                builder,
                " getattrs=",
                reader,
                provider,
                ReturnParameterAttributes(
                    reader,
                    reader.GetMethodDefinition(accessors.Getter).GetParameters()
                ),
                "getter of " + owner
            );
        }

        if (setter != "none")
        {
            AppendFlowAttributes(
                builder,
                " setattrs=",
                reader,
                provider,
                SetterValueParameterAttributes(reader, accessors.Setter),
                "setter of " + owner
            );
        }

        AppendFlowAttributes(builder, " propattrs=", reader, provider, property.GetCustomAttributes(), owner);

        line = builder.ToString();

        return true;
    }

    /// <summary>
    /// The attributes on a method's return parameter row (sequence 0), or an empty collection when
    /// the compiler emitted no such row, which it does only when nothing is attached to the return.
    /// </summary>
    private static CustomAttributeHandleCollection? ReturnParameterAttributes(
        MetadataReader reader,
        ParameterHandleCollection parameters
    )
    {
        foreach (ParameterHandle handle in parameters)
        {
            Parameter parameter = reader.GetParameter(handle);

            if (parameter.SequenceNumber == 0)
            {
                return parameter.GetCustomAttributes();
            }
        }

        return null;
    }

    /// <summary>
    /// The attributes on a setter's value parameter row, which is its last parameter; the compiler
    /// places a property's [AllowNull] and [DisallowNull] there.
    /// </summary>
    private static CustomAttributeHandleCollection? SetterValueParameterAttributes(
        MetadataReader reader,
        MethodDefinitionHandle setter
    )
    {
        MethodDefinition method = reader.GetMethodDefinition(setter);
        int valueSequence = method.GetParameters().Count;
        CustomAttributeHandleCollection? result = null;
        int highest = 0;

        foreach (ParameterHandle handle in method.GetParameters())
        {
            Parameter parameter = reader.GetParameter(handle);

            if (parameter.SequenceNumber > highest && parameter.SequenceNumber <= valueSequence)
            {
                highest = parameter.SequenceNumber;
                result = parameter.GetCustomAttributes();
            }
        }

        return result;
    }

    /// <summary>
    /// Renders one signature's parameters, taking names, modifiers and annotations from the owning
    /// method's parameter rows, and the nullable context from that method outward.
    /// </summary>
    private static List<string> DescribeSignatureParameters(
        MetadataReader reader,
        ContractSignatureProvider provider,
        ImmutableArray<string> parameterTypes,
        ImmutableArray<NullabilityShape> parameterShapes,
        ParameterHandleCollection owned,
        MethodDefinitionHandle contextOwner,
        string owner
    )
    {
        Dictionary<int, Parameter> parameters = [];

        foreach (ParameterHandle handle in owned)
        {
            Parameter parameter = reader.GetParameter(handle);

            // Sequence 0 is the return parameter, which carries marshalling and attributes rather
            // than a position in the argument list.
            if (parameter.SequenceNumber > 0)
            {
                parameters[parameter.SequenceNumber] = parameter;
            }
        }

        if (parameterShapes.Length != parameterTypes.Length)
        {
            throw new BadImageFormatException(
                $"The signature of {owner} decoded to {parameterTypes.Length} parameter types and {parameterShapes.Length} nullability shapes."
            );
        }

        List<string> rendered = [];

        for (int index = 0; index < parameterTypes.Length; index++)
        {
            rendered.Add(
                DescribeParameter(
                    reader,
                    provider,
                    parameterTypes[index],
                    parameterShapes[index],
                    parameters.TryGetValue(index + 1, out Parameter parameter) ? parameter : null,
                    ContextChainForMethod(reader, contextOwner),
                    owner
                )
            );
        }

        return rendered;
    }

    // init is an ordinary setter carrying a required modifier of IsExternalInit on its return type,
    // so nothing in the accessor's name, accessibility or modifiers distinguishes it from set. The
    // difference is the whole contract for a consumer that assigns the property after construction.
    private static string SetterKind(
        MetadataReader reader,
        ContractSignatureProvider provider,
        MethodDefinitionHandle handle,
        GenericContext context
    )
    {
        if (handle.IsNil)
        {
            return "none";
        }

        MethodDefinition setter = reader.GetMethodDefinition(handle);

        if (!IsVisible(setter.Attributes))
        {
            return "none";
        }

        MethodSignature<string> signature = setter.DecodeSignature(provider, context);

        return signature.ReturnType.Contains(
            "System.Runtime.CompilerServices.IsExternalInit",
            StringComparison.Ordinal
        )
            ? "init"
            : "set";
    }

    private static bool TryDescribeEvent(
        MetadataReader reader,
        ContractSignatureProvider provider,
        EventDefinitionHandle handle,
        string typeName,
        GenericContext context,
        out string line
    )
    {
        line = string.Empty;

        EventDefinition eventDefinition = reader.GetEventDefinition(handle);
        EventAccessors accessors = eventDefinition.GetAccessors();

        string adder = AccessorAccessibility(reader, accessors.Adder);
        string remover = AccessorAccessibility(reader, accessors.Remover);

        if (adder == "none" && remover == "none")
        {
            return false;
        }

        StringBuilder builder = new();
        string eventName = reader.GetString(eventDefinition.Name);
        builder.Append("EVENT ").Append(typeName).Append('.').Append(eventName);
        builder.Append(" : ").Append(TypeName(reader, provider, eventDefinition.Type, context));

        // An event's type is a delegate, so a definition or reference handle is one annotatable
        // position; a constructed delegate type arrives as a specification and is decoded.
        NullabilityShape shape =
            eventDefinition.Type.Kind == HandleKind.TypeSpecification
                ? reader
                    .GetTypeSpecification((TypeSpecificationHandle)eventDefinition.Type)
                    .DecodeSignature(ShapeProvider, context)
                : NullabilityShape.Annotatable;

        // The event row, like a property row, is governed by the declaring type's context.
        MethodDefinitionHandle eventAccessor = accessors.Adder.IsNil ? accessors.Remover : accessors.Adder;

        builder
            .Append(" nullable=")
            .Append(
                DescribeNullability(
                    reader,
                    provider,
                    eventDefinition.GetCustomAttributes(),
                    ContextChainForType(reader, reader.GetMethodDefinition(eventAccessor).GetDeclaringType()),
                    shape,
                    "event '" + typeName + "." + eventName + "'"
                )
            );
        builder.Append(" add=").Append(adder);
        builder.Append(" remove=").Append(remover);

        line = builder.ToString();

        return true;
    }

    /// <summary>
    /// The effective nullability of one type as a vector with one entry per annotatable-or-fixed
    /// position, computed so that the same source renders identically however the compiler packed
    /// its attributes.
    /// </summary>
    /// <remarks>
    /// An explicit NullableAttribute byte[] is the vector itself, and must have exactly as many
    /// entries as the shape has positions, with 0 at every fixed position. An explicit byte applies
    /// to every annotatable position. No attribute means the effective NullableContext, resolved
    /// outward through <paramref name="contextChain"/>, at every annotatable position, or 0
    /// everywhere when the type has no annotatable position, which is why an int parameter reads
    /// the same under every context. A single byte and an elided attribute both mean "every
    /// annotatable position agrees", so explicit and elided encodings of one annotation are equal.
    /// </remarks>
    private static string DescribeNullability(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttributeHandleCollection? ownRow,
        IEnumerable<CustomAttributeHandleCollection> contextChain,
        NullabilityShape shape,
        string owner
    )
    {
        int count = shape.Positions.Length;
        byte[] vector = new byte[count];

        if (
            ownRow is { } row
            && TryReadNullableFlags(
                reader,
                provider,
                row,
                owner,
                out byte single,
                out ImmutableArray<byte>? explicitFlags
            )
        )
        {
            if (explicitFlags is { } flags)
            {
                if (flags.Length != count)
                {
                    throw new BadImageFormatException(
                        $"NullableAttribute on {owner} carries {flags.Length} flag(s) for a type with {count} nullability position(s)."
                    );
                }

                for (int index = 0; index < count; index++)
                {
                    if (!shape.Positions[index] && flags[index] != 0)
                    {
                        throw new BadImageFormatException(
                            $"NullableAttribute on {owner} annotates position {index} with {flags[index]}, but that position is a value type or pointer and can only be 0."
                        );
                    }

                    vector[index] = flags[index];
                }
            }
            else
            {
                for (int index = 0; index < count; index++)
                {
                    vector[index] = shape.Positions[index] ? single : (byte)0;
                }
            }
        }
        else
        {
            byte context = shape.HasAnnotatablePosition
                ? EffectiveNullableContext(reader, provider, contextChain, owner)
                : (byte)0;

            for (int index = 0; index < count; index++)
            {
                vector[index] = shape.Positions[index] ? context : (byte)0;
            }
        }

        return "["
            + string.Join(
                ",",
                vector.Select(flag => flag.ToString(System.Globalization.CultureInfo.InvariantCulture))
            )
            + "]";
    }

    /// <summary>
    /// The first NullableContextAttribute found walking <paramref name="contextChain"/>, or 0
    /// (oblivious) when none of the entities carries one.
    /// </summary>
    private static byte EffectiveNullableContext(
        MetadataReader reader,
        ContractSignatureProvider provider,
        IEnumerable<CustomAttributeHandleCollection> contextChain,
        string owner
    )
    {
        foreach (CustomAttributeHandleCollection attributes in contextChain)
        {
            if (
                TryReadNullableByte(
                    reader,
                    provider,
                    attributes,
                    "NullableContextAttribute",
                    owner,
                    out byte value
                )
            )
            {
                return value;
            }
        }

        return 0;
    }

    /// <summary>
    /// The entities whose NullableContextAttribute can govern a method's parameters and return,
    /// nearest first: the method, its declaring type, and every enclosing type outward.
    /// </summary>
    private static IEnumerable<CustomAttributeHandleCollection> ContextChainForMethod(
        MetadataReader reader,
        MethodDefinitionHandle handle
    )
    {
        if (handle.IsNil)
        {
            yield break;
        }

        MethodDefinition method = reader.GetMethodDefinition(handle);
        yield return method.GetCustomAttributes();

        foreach (
            CustomAttributeHandleCollection attributes in ContextChainForType(
                reader,
                method.GetDeclaringType()
            )
        )
        {
            yield return attributes;
        }
    }

    /// <summary>
    /// The declaring type and every enclosing type outward. This is the whole chain for a property,
    /// event or field row, since NullableContextAttribute cannot target those.
    /// </summary>
    private static IEnumerable<CustomAttributeHandleCollection> ContextChainForType(
        MetadataReader reader,
        TypeDefinitionHandle handle
    )
    {
        while (!handle.IsNil)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            yield return type.GetCustomAttributes();
            handle = type.GetDeclaringType();
        }
    }

    /// <summary>
    /// Reads a NullableAttribute in either of its two forms. The byte[] form's element count is
    /// validated against the blob and every flag against the three values the compiler defines.
    /// </summary>
    private static bool TryReadNullableFlags(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttributeHandleCollection attributes,
        string owner,
        out byte single,
        out ImmutableArray<byte>? explicitFlags
    )
    {
        const string AttributeName = "NullableAttribute";

        single = 0;
        explicitFlags = null;

        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);

            if (!TryGetAttributeTypeName(reader, attribute, out string attributeNamespace, out string name))
            {
                continue;
            }

            if (attributeNamespace != "System.Runtime.CompilerServices" || name != AttributeName)
            {
                continue;
            }

            ImmutableArray<string> parameterTypes = AttributeConstructorParameterTypes(
                reader,
                provider,
                attribute,
                AttributeName,
                owner
            );

            if (
                parameterTypes.Length != 1
                || (parameterTypes[0] != "System.Byte" && parameterTypes[0] != "System.Byte[]")
            )
            {
                throw new BadImageFormatException(
                    $"{AttributeName} on {owner} has constructor ({string.Join(", ", parameterTypes)}); this reader supports the byte and byte[] forms."
                );
            }

            BlobReader blob = ReadAttributeProlog(reader, attribute, AttributeName, owner);

            if (parameterTypes[0] == "System.Byte")
            {
                RequireArgumentBytes(ref blob, sizeof(byte), AttributeName, owner);
                single = RequireNullableFlag(blob.ReadByte(), AttributeName, owner);
                RequireNoNamedArguments(ref blob, AttributeName, owner);

                return true;
            }

            // The array form: a 32-bit element count, then one byte per position. 0xFFFFFFFF is
            // how a null array is encoded, and a null array is not an annotation.
            RequireArgumentBytes(ref blob, sizeof(uint), AttributeName, owner);
            uint length = blob.ReadUInt32();

            if (length == uint.MaxValue)
            {
                throw new BadImageFormatException($"{AttributeName} on {owner} carries a null flag array.");
            }

            if (length > int.MaxValue || length > (uint)blob.RemainingBytes)
            {
                throw new BadImageFormatException(
                    $"{AttributeName} on {owner} declares {length} flag(s) but only {blob.RemainingBytes} argument byte(s) remain."
                );
            }

            ImmutableArray<byte>.Builder flags = ImmutableArray.CreateBuilder<byte>((int)length);

            for (uint index = 0; index < length; index++)
            {
                flags.Add(RequireNullableFlag(blob.ReadByte(), AttributeName, owner));
            }

            RequireNoNamedArguments(ref blob, AttributeName, owner);
            explicitFlags = flags.MoveToImmutable();

            return true;
        }

        return false;
    }

    // 0 (oblivious), 1 (not annotated) and 2 (annotated) are the only values the compiler defines
    // for a nullability flag; anything else is a corrupt or foreign encoding, not a fourth state.
    private static byte RequireNullableFlag(byte value, string attributeName, string owner)
    {
        if (value > 2)
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} carries flag {value}; only 0, 1 and 2 are defined."
            );
        }

        return value;
    }

    /// <summary>
    /// Appends the nullable-flow attributes found on one row, rendered as a sorted list under the
    /// given label, or nothing when the row carries none.
    /// </summary>
    private static void AppendFlowAttributes(
        StringBuilder builder,
        string label,
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttributeHandleCollection? attributes,
        string owner
    )
    {
        if (attributes is not { } row)
        {
            return;
        }

        string rendered = DescribeFlowAttributes(reader, provider, row, owner);

        if (rendered.Length > 0)
        {
            builder.Append(label).Append(rendered);
        }
    }

    /// <summary>
    /// The System.Diagnostics.CodeAnalysis nullable-flow attributes on one row, as
    /// <c>[Name,Name:value,...]</c> sorted ordinally, or an empty string.
    /// </summary>
    /// <remarks>
    /// Each of these constructors takes nothing, one bool or one string, and that is checked
    /// against the constructor's own signature before the blob is read; another shape, a bool
    /// that is not 0 or 1, a null string or named arguments are refused. These attributes are
    /// written explicitly in source and the compiler never elides or relocates them, so their
    /// rendering cannot differ for one source compiled twice.
    /// </remarks>
    private static string DescribeFlowAttributes(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttributeHandleCollection attributes,
        string owner
    )
    {
        List<string> rendered = [];

        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);

            if (!TryGetAttributeTypeName(reader, attribute, out string attributeNamespace, out string name))
            {
                continue;
            }

            if (attributeNamespace != "System.Diagnostics.CodeAnalysis" || !FlowAttributeNames.Contains(name))
            {
                continue;
            }

            string shortName = name[..^"Attribute".Length];
            ImmutableArray<string> parameterTypes = AttributeConstructorParameterTypes(
                reader,
                provider,
                attribute,
                name,
                owner
            );
            BlobReader blob = ReadAttributeProlog(reader, attribute, name, owner);
            string value;

            switch (parameterTypes.Length)
            {
                case 0:
                    value = string.Empty;

                    break;

                case 1 when parameterTypes[0] == "System.Boolean":
                    RequireArgumentBytes(ref blob, sizeof(byte), name, owner);
                    value = blob.ReadByte() switch
                    {
                        0 => "false",
                        1 => "true",
                        byte other => throw new BadImageFormatException(
                            $"{name} on {owner} carries boolean value {other}; only 0 and 1 are defined."
                        ),
                    };

                    break;

                case 1 when parameterTypes[0] == "System.String":
                    RequireArgumentBytes(ref blob, sizeof(byte), name, owner);
                    value =
                        blob.ReadSerializedString()
                        ?? throw new BadImageFormatException(
                            $"{name} on {owner} carries a null string argument."
                        );

                    break;

                default:
                    throw new BadImageFormatException(
                        $"{name} on {owner} has constructor ({string.Join(", ", parameterTypes)}); this reader supports (), (bool) and (string)."
                    );
            }

            RequireNoNamedArguments(ref blob, name, owner);
            rendered.Add(value.Length == 0 ? shortName : shortName + ":" + value);
        }

        if (rendered.Count == 0)
        {
            return string.Empty;
        }

        rendered.Sort(StringComparer.Ordinal);

        return "[" + string.Join(",", rendered) + "]";
    }

    private static string DescribeGenericParameters(
        MetadataReader reader,
        ContractSignatureProvider provider,
        GenericParameterHandleCollection handles,
        GenericContext context
    )
    {
        if (handles.Count == 0)
        {
            return "none";
        }

        List<string> described = [];

        foreach (GenericParameterHandle handle in handles)
        {
            GenericParameter parameter = reader.GetGenericParameter(handle);
            StringBuilder builder = new();

            GenericParameterAttributes variance =
                parameter.Attributes & GenericParameterAttributes.VarianceMask;
            if (variance == GenericParameterAttributes.Covariant)
            {
                builder.Append("out ");
            }
            else if (variance == GenericParameterAttributes.Contravariant)
            {
                builder.Append("in ");
            }

            builder.Append(reader.GetString(parameter.Name));

            List<string> constraints = [];
            GenericParameterAttributes special =
                parameter.Attributes & GenericParameterAttributes.SpecialConstraintMask;

            if (special.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                constraints.Add("class");
            }
            if (special.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                constraints.Add("struct");
            }
            if (special.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint))
            {
                constraints.Add("new()");
            }

            foreach (GenericParameterConstraintHandle constraintHandle in parameter.GetConstraints())
            {
                GenericParameterConstraint constraint = reader.GetGenericParameterConstraint(
                    constraintHandle
                );
                constraints.Add(TypeName(reader, provider, constraint.Type, context));
            }

            string annotation = NullableAnnotation(reader, provider, parameter);

            if (annotation.Length > 0)
            {
                constraints.Add(annotation);
            }

            constraints.Sort(StringComparer.Ordinal);

            if (constraints.Count > 0)
            {
                builder.Append(':').Append(string.Join("+", constraints));
            }

            described.Add(builder.ToString());
        }

        return string.Join(",", described);
    }

    /// <summary>
    /// The full metadata name of a type definition, with nested types joined by '+'.
    /// </summary>
    public static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();

        if (!declaring.IsNil)
        {
            return FullName(reader, declaring) + "+" + name;
        }

        string typeNamespace = reader.GetString(type.Namespace);

        return string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name;
    }

    /// <summary>
    /// The full metadata name of a type reference, with nested types joined by '+'.
    /// </summary>
    public static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return FullName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        }

        string typeNamespace = reader.GetString(type.Namespace);

        return string.IsNullOrEmpty(typeNamespace) ? name : typeNamespace + "." + name;
    }

    private static string TypeName(
        MetadataReader reader,
        ContractSignatureProvider provider,
        EntityHandle handle,
        GenericContext context
    ) =>
        handle.IsNil
            ? "none"
            : handle.Kind switch
            {
                HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)handle),
                HandleKind.TypeReference => FullName(reader, (TypeReferenceHandle)handle),
                HandleKind.TypeSpecification => reader
                    .GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(provider, context),
                _ => "unknown",
            };

    private static bool IsVisible(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;

        if (visibility == TypeAttributes.Public)
        {
            return true;
        }

        // A visible nested type inside an invisible one is not reachable from outside, so the whole
        // declaring chain has to be visible before the nested type counts.
        if (
            visibility
            is TypeAttributes.NestedPublic
                or TypeAttributes.NestedFamily
                or TypeAttributes.NestedFamORAssem
        )
        {
            TypeDefinitionHandle declaring = type.GetDeclaringType();

            return !declaring.IsNil && IsVisible(reader, declaring);
        }

        return false;
    }

    // FamANDAssem (private protected) is never visible; an abstract one is recorded by
    // ClosesHierarchy instead, see the file header. A member that moves between private protected
    // abstract and protected abstract is still a change: its own line appears, and the CLOSURE
    // line disappears when it was the only closing member.
    private static bool IsVisible(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask)
            is MethodAttributes.Public
                or MethodAttributes.Family
                or MethodAttributes.FamORAssem;

    // True when any method of the type, accessors included, is private protected and abstract. No
    // type outside the assembly can implement such a member, so the type cannot be derived from
    // outside, whatever else it exposes.
    private static bool ClosesHierarchy(MetadataReader reader, TypeDefinition type)
    {
        foreach (MethodDefinitionHandle handle in type.GetMethods())
        {
            MethodAttributes attributes = reader.GetMethodDefinition(handle).Attributes;

            if (
                (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.FamANDAssem
                && attributes.HasFlag(MethodAttributes.Abstract)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVisible(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask)
            is FieldAttributes.Public
                or FieldAttributes.Family
                or FieldAttributes.FamORAssem;

    private static string TypeAccessibility(TypeAttributes attributes) =>
        (attributes & TypeAttributes.VisibilityMask) switch
        {
            TypeAttributes.Public => "public",
            TypeAttributes.NestedPublic => "public",
            TypeAttributes.NestedFamily => "protected",
            TypeAttributes.NestedFamORAssem => "protected internal",
            _ => "other",
        };

    private static string MemberAccessibility(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Public => "public",
            MethodAttributes.Family => "protected",
            MethodAttributes.FamORAssem => "protected internal",
            MethodAttributes.FamANDAssem => "private protected",
            _ => "other",
        };

    private static string FieldAccessibility(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask) switch
        {
            FieldAttributes.Public => "public",
            FieldAttributes.Family => "protected",
            FieldAttributes.FamORAssem => "protected internal",
            _ => "other",
        };

    private static string MethodModifiers(MethodAttributes attributes)
    {
        List<string> modifiers = [];

        if (attributes.HasFlag(MethodAttributes.Static))
        {
            modifiers.Add("static");
        }
        if (attributes.HasFlag(MethodAttributes.Abstract))
        {
            modifiers.Add("abstract");
        }
        if (attributes.HasFlag(MethodAttributes.Virtual))
        {
            modifiers.Add(attributes.HasFlag(MethodAttributes.NewSlot) ? "virtual" : "override");
        }
        if (attributes.HasFlag(MethodAttributes.Final))
        {
            modifiers.Add("sealed");
        }

        return modifiers.Count == 0 ? "none" : string.Join(",", modifiers);
    }

    // Accessibility and modifiers together, because abstract and virtual decide whether a deriving
    // implementer must override the accessor or may, and a property whose getter stopped being
    // abstract is a different contract at the same accessibility.
    private static string AccessorAccessibility(MetadataReader reader, MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
        {
            return "none";
        }

        MethodDefinition accessor = reader.GetMethodDefinition(handle);

        if (!IsVisible(accessor.Attributes))
        {
            return "none";
        }

        return MemberAccessibility(accessor.Attributes) + ":" + MethodModifiers(accessor.Attributes);
    }

    private static string TypeKind(MetadataReader reader, TypeDefinition type, TypeAttributes attributes)
    {
        if (attributes.HasFlag(TypeAttributes.Interface))
        {
            return "interface";
        }

        string baseName = BaseTypeName(reader, type);

        return baseName switch
        {
            "System.Enum" => "enum",
            "System.ValueType" => "struct",
            "System.MulticastDelegate" or "System.Delegate" => "delegate",
            _ => "class",
        };
    }

    private static bool IsEnum(MetadataReader reader, TypeDefinition type) =>
        BaseTypeName(reader, type) == "System.Enum";

    // The nil check comes first and is not redundant. An interface has no base type, and a nil
    // EntityHandle reports its Kind as TypeDefinition, so switching on Kind alone casts nil to a
    // row handle and the metadata reader reads past the end of the table.
    private static string BaseTypeName(MetadataReader reader, TypeDefinition type)
    {
        EntityHandle handle = type.BaseType;

        if (handle.IsNil)
        {
            return string.Empty;
        }

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => FullName(reader, (TypeReferenceHandle)handle),
            _ => string.Empty,
        };
    }

    // An enum's underlying type is the type of its one instance field, which is not part of the
    // visible member set but does change what a consumer can assign.
    private static string EnumUnderlyingType(
        MetadataReader reader,
        ContractSignatureProvider provider,
        TypeDefinition type,
        GenericContext context
    )
    {
        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(handle);

            if (!field.Attributes.HasFlag(FieldAttributes.Static))
            {
                return field.DecodeSignature(provider, context);
            }
        }

        return "unknown";
    }

    // The declared names, in declaration order, so a decoded signature reads TValue rather than !0
    // and renaming a type parameter is the visible change it is to anyone implementing the type.
    private static ImmutableArray<string> GenericParameterNames(
        MetadataReader reader,
        GenericParameterHandleCollection handles
    )
    {
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(handles.Count);

        foreach (GenericParameterHandle handle in handles)
        {
            names.Add(reader.GetString(reader.GetGenericParameter(handle).Name));
        }

        return names.ToImmutable();
    }

    // Whether each generic parameter carries the struct constraint (NotNullableValueTypeConstraint,
    // which `where T : struct` and `where T : unmanaged` both set), in declaration order.
    private static ImmutableArray<bool> GenericParameterValueTypeFlags(
        MetadataReader reader,
        GenericParameterHandleCollection handles
    )
    {
        ImmutableArray<bool>.Builder flags = ImmutableArray.CreateBuilder<bool>(handles.Count);

        foreach (GenericParameterHandle handle in handles)
        {
            flags.Add(
                reader
                    .GetGenericParameter(handle)
                    .Attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)
            );
        }

        return flags.ToImmutable();
    }

    private static void AddAccessor(HashSet<int> accessors, MethodDefinitionHandle handle)
    {
        if (!handle.IsNil)
        {
            accessors.Add(MetadataTokens.GetRowNumber(handle));
        }
    }

    /// <summary>
    /// A member's compile-time value, from the metadata Constant table when there is one and from
    /// the attribute that encodes it when there is not.
    /// </summary>
    /// <remarks>
    /// The Constant table cannot hold every constant a C# signature can declare. A decimal default
    /// or a decimal constant is carried by DecimalConstantAttribute and a DateTime default by
    /// DateTimeConstantAttribute, because neither type has a CLR literal encoding. Reading only the
    /// Constant table reports both sides of a changed decimal default as having no default at all,
    /// and a consumer compiles that value into its own code.
    ///
    /// Everything here is read from metadata; no attribute is instantiated and no code from the
    /// assembly under inspection runs.
    /// </remarks>
    private static string DescribeDefaultValue(
        MetadataReader reader,
        ContractSignatureProvider provider,
        ConstantHandle constant,
        CustomAttributeHandleCollection attributes,
        string owner
    )
    {
        if (!constant.IsNil)
        {
            return DescribeConstant(reader, constant);
        }

        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);

            if (
                !TryGetAttributeTypeName(
                    reader,
                    attribute,
                    out string attributeNamespace,
                    out string attributeName
                )
            )
            {
                continue;
            }

            if (attributeNamespace != "System.Runtime.CompilerServices")
            {
                continue;
            }

            switch (attributeName)
            {
                case "DecimalConstantAttribute":
                    return DescribeDecimalConstant(reader, provider, attribute, owner);

                case "DateTimeConstantAttribute":
                    return DescribeDateTimeConstant(reader, provider, attribute, owner);

                default:
                    // An encoding this reader does not decode is carried as its name plus the raw
                    // argument bytes rather than dropped. Two different values then never compare
                    // equal, which is the property that matters for a publish gate, even though the
                    // rendering is not human-readable.
                    if (attributeName.EndsWith("ConstantAttribute", StringComparison.Ordinal))
                    {
                        return attributeName
                            + ":raw:"
                            + Convert.ToHexString(reader.GetBlobBytes(attribute.Value));
                    }

                    break;
            }
        }

        return "none";
    }

    // Both DecimalConstantAttribute constructors lay their arguments out identically: a two-byte
    // prolog, scale, sign, then the high, middle and low 32-bit words.
    private static string DescribeDecimalConstant(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttribute attribute,
        string owner
    )
    {
        const string AttributeName = "DecimalConstantAttribute";

        // Both documented constructors take (byte scale, byte sign, X hi, X mid, X lo) where X is
        // uint or int, and both lay their arguments out identically. The shape is checked rather
        // than assumed, so an unrecognized constructor is a failure rather than five bytes read at
        // the wrong offsets.
        ImmutableArray<string> parameterTypes = AttributeConstructorParameterTypes(
            reader,
            provider,
            attribute,
            AttributeName,
            owner
        );

        if (
            parameterTypes.Length != 5
            || parameterTypes[0] != "System.Byte"
            || parameterTypes[1] != "System.Byte"
            || !IsWordType(parameterTypes[2])
            || !IsWordType(parameterTypes[3])
            || !IsWordType(parameterTypes[4])
        )
        {
            throw new BadImageFormatException(
                $"{AttributeName} on {owner} has constructor ({string.Join(", ", parameterTypes)}); this reader supports (byte, byte, uint, uint, uint) and its int form."
            );
        }

        BlobReader blob = ReadAttributeProlog(reader, attribute, AttributeName, owner);

        const int FixedArgumentBytes = sizeof(byte) + sizeof(byte) + (3 * sizeof(uint));
        RequireArgumentBytes(ref blob, FixedArgumentBytes, AttributeName, owner);

        byte scale = blob.ReadByte();
        byte sign = blob.ReadByte();
        uint high = blob.ReadUInt32();
        uint middle = blob.ReadUInt32();
        uint low = blob.ReadUInt32();

        RequireNoNamedArguments(ref blob, AttributeName, owner);

        // The decimal constructor rejects a scale above 28, and rejecting it here names the member
        // instead of surfacing an ArgumentOutOfRangeException from three frames down. The sign byte
        // needs no such check: DecimalConstantAttribute treats any non-zero value as negative, which
        // is what the bool below passes on.
        if (scale > 28)
        {
            throw new BadImageFormatException(
                $"{AttributeName} on {owner} declares scale {scale}; a decimal scale is at most 28."
            );
        }

        decimal value = new(
            unchecked((int)low),
            unchecked((int)middle),
            unchecked((int)high),
            sign != 0,
            scale
        );

        return "decimal:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static bool IsWordType(string type) => type is "System.UInt32" or "System.Int32";
    }

    private static string DescribeDateTimeConstant(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttribute attribute,
        string owner
    )
    {
        const string AttributeName = "DateTimeConstantAttribute";

        ImmutableArray<string> parameterTypes = AttributeConstructorParameterTypes(
            reader,
            provider,
            attribute,
            AttributeName,
            owner
        );

        if (parameterTypes.Length != 1 || parameterTypes[0] != "System.Int64")
        {
            throw new BadImageFormatException(
                $"{AttributeName} on {owner} has constructor ({string.Join(", ", parameterTypes)}); this reader supports (long)."
            );
        }

        BlobReader blob = ReadAttributeProlog(reader, attribute, AttributeName, owner);
        RequireArgumentBytes(ref blob, sizeof(long), AttributeName, owner);

        long ticks = blob.ReadInt64();

        RequireNoNamedArguments(ref blob, AttributeName, owner);

        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            throw new BadImageFormatException(
                $"{AttributeName} on {owner} declares {ticks} ticks, which is outside the representable range."
            );
        }

        // Ticks rather than a formatted date: a format would drag in a calendar and a culture, and
        // the tick count is what the compiler recorded.
        return "datetime:" + ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The nullable annotation a generic parameter carries, which is how <c>notnull</c> is recorded.
    /// </summary>
    /// <remarks>
    /// notnull is not a CLR constraint flag. The compiler records it as NullableAttribute(1) on the
    /// type parameter, so a reader that looked only at GenericParameterAttributes and the constraint
    /// table reports `where T : notnull` and an unconstrained T identically. The attribute can be
    /// elided in favour of a NullableContextAttribute on the declaring type or method, which is why
    /// that fallback exists.
    /// </remarks>
    private static string NullableAnnotation(
        MetadataReader reader,
        ContractSignatureProvider provider,
        GenericParameter parameter
    )
    {
        string owner = "type parameter '" + reader.GetString(parameter.Name) + "'";

        // The parameter's own annotation wins when it has one. The compiler emits it only when it
        // differs from the context in force, which is why the chain below is not optional.
        if (
            TryReadNullableByte(
                reader,
                provider,
                parameter.GetCustomAttributes(),
                "NullableAttribute",
                owner,
                out byte value
            )
        )
        {
            return Describe(value);
        }

        // The effective NullableContextAttribute, resolved outward. Which entity carries it is a
        // compiler packing decision rather than a fact about the contract: adding a private member
        // can move it from a method onto the declaring type, and a reader that looked only at the
        // method would then report an unchanged public constraint as changed and demand a version
        // bump for a change no consumer can see.
        foreach (CustomAttributeHandleCollection attributes in ContextAttributeChain(reader, parameter))
        {
            if (
                TryReadNullableByte(
                    reader,
                    provider,
                    attributes,
                    "NullableContextAttribute",
                    owner,
                    out value
                )
            )
            {
                return Describe(value);
            }
        }

        return string.Empty;

        static string Describe(byte value) =>
            value switch
            {
                1 => "notnull",
                _ => "nullable(" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
            };
    }

    /// <summary>
    /// The entities whose NullableContextAttribute can govern a generic parameter, nearest first:
    /// its declaring method, that method's declaring type, and every enclosing type outward.
    /// </summary>
    private static IEnumerable<CustomAttributeHandleCollection> ContextAttributeChain(
        MetadataReader reader,
        GenericParameter parameter
    ) =>
        parameter.Parent.Kind switch
        {
            HandleKind.MethodDefinition => ContextChainForMethod(
                reader,
                (MethodDefinitionHandle)parameter.Parent
            ),
            HandleKind.TypeDefinition => ContextChainForType(reader, (TypeDefinitionHandle)parameter.Parent),
            _ => [],
        };

    private static bool TryReadNullableByte(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttributeHandleCollection attributes,
        string attributeName,
        string owner,
        out byte value
    )
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);

            if (!TryGetAttributeTypeName(reader, attribute, out string attributeNamespace, out string name))
            {
                continue;
            }

            if (attributeNamespace != "System.Runtime.CompilerServices" || name != attributeName)
            {
                continue;
            }

            // Which overload was used is read from the constructor's own signature rather than
            // guessed from the blob's length. Both NullableAttribute forms begin with the same two
            // prolog bytes, and under the byte[] form the next four bytes are an element count, so a
            // reader that assumed the third byte was the value would report an element count as an
            // annotation.
            ImmutableArray<string> parameterTypes = AttributeConstructorParameterTypes(
                reader,
                provider,
                attribute,
                attributeName,
                owner
            );

            if (parameterTypes.Length != 1)
            {
                throw new BadImageFormatException(
                    $"{attributeName} on {owner} declares {parameterTypes.Length} constructor parameters; this reader supports the byte and byte[] forms."
                );
            }

            // The array form annotates a constructed type rather than a bare type parameter, so it
            // states nothing about a constraint. It is not an error, and it is not an annotation.
            if (parameterTypes[0] == "System.Byte[]")
            {
                continue;
            }

            if (parameterTypes[0] != "System.Byte")
            {
                throw new BadImageFormatException(
                    $"{attributeName} on {owner} takes {parameterTypes[0]}; this reader supports the byte and byte[] forms."
                );
            }

            BlobReader blob = ReadAttributeProlog(reader, attribute, attributeName, owner);
            RequireArgumentBytes(ref blob, sizeof(byte), attributeName, owner);
            value = RequireNullableFlag(blob.ReadByte(), attributeName, owner);
            RequireNoNamedArguments(ref blob, attributeName, owner);

            return true;
        }

        value = 0;

        return false;
    }

    /// <summary>
    /// The decoded parameter types of the constructor a custom attribute names.
    /// </summary>
    private static ImmutableArray<string> AttributeConstructorParameterTypes(
        MetadataReader reader,
        ContractSignatureProvider provider,
        CustomAttribute attribute,
        string attributeName,
        string owner
    ) =>
        attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader
                .GetMemberReference((MemberReferenceHandle)attribute.Constructor)
                .DecodeMethodSignature(provider, GenericContext.Empty)
                .ParameterTypes,
            HandleKind.MethodDefinition => reader
                .GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor)
                .DecodeSignature(provider, GenericContext.Empty)
                .ParameterTypes,
            _ => throw new BadImageFormatException(
                $"{attributeName} on {owner} names a constructor this reader cannot resolve."
            ),
        };

    // A custom attribute blob opens with a two-byte prolog of 0x0001. Anything else is corrupt, and
    // corrupt is a failure rather than a value: a sentinel string would make two differently corrupt
    // inputs compare equal, which is exactly what a publish gate must never do.
    private static BlobReader ReadAttributeProlog(
        MetadataReader reader,
        CustomAttribute attribute,
        string attributeName,
        string owner
    )
    {
        BlobReader blob = reader.GetBlobReader(attribute.Value);

        if (blob.RemainingBytes < sizeof(ushort))
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} carries a {blob.Length}-byte argument blob, too short for its prolog."
            );
        }

        ushort prolog = blob.ReadUInt16();

        if (prolog != 1)
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} carries prolog 0x{prolog:X4} rather than 0x0001, so its arguments cannot be decoded."
            );
        }

        return blob;
    }

    // Validates only, and deliberately returns nothing. An earlier revision returned the reader,
    // which is a struct: the caller then read its argument out of a copy and left the real reader
    // positioned on the value, so the named-argument count was read from the middle of the payload.
    private static void RequireArgumentBytes(
        ref BlobReader blob,
        int byteCount,
        string attributeName,
        string owner
    )
    {
        if (blob.RemainingBytes < byteCount)
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} is truncated: {byteCount} more argument byte(s) were required and {blob.RemainingBytes} remain."
            );
        }
    }

    // The named-argument count closes every attribute blob. Requiring it, and requiring it to be
    // zero for these constructors, is what makes a truncated-but-plausible payload a failure instead
    // of a silently short read.
    private static void RequireNoNamedArguments(ref BlobReader blob, string attributeName, string owner)
    {
        if (blob.RemainingBytes < sizeof(ushort))
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} is truncated: its named-argument count is missing."
            );
        }

        ushort named = blob.ReadUInt16();

        if (named != 0)
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} declares {named} named argument(s); this reader supports none."
            );
        }

        if (blob.RemainingBytes != 0)
        {
            throw new BadImageFormatException(
                $"{attributeName} on {owner} carries {blob.RemainingBytes} unread argument byte(s)."
            );
        }
    }

    private static bool TryGetAttributeTypeName(
        MetadataReader reader,
        CustomAttribute attribute,
        out string attributeNamespace,
        out string attributeName
    )
    {
        attributeNamespace = string.Empty;
        attributeName = string.Empty;

        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                MemberReference member = reader.GetMemberReference(
                    (MemberReferenceHandle)attribute.Constructor
                );

                if (member.Parent.Kind != HandleKind.TypeReference)
                {
                    return false;
                }

                TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                attributeNamespace = reader.GetString(reference.Namespace);
                attributeName = reader.GetString(reference.Name);

                return true;

            // An attribute declared in the assembly being read, which is how the compiler emits its
            // own NullableAttribute into an assembly that does not reference one.
            case HandleKind.MethodDefinition:
                MethodDefinition constructor = reader.GetMethodDefinition(
                    (MethodDefinitionHandle)attribute.Constructor
                );
                TypeDefinition declaring = reader.GetTypeDefinition(constructor.GetDeclaringType());
                attributeNamespace = reader.GetString(declaring.Namespace);
                attributeName = reader.GetString(declaring.Name);

                return true;

            default:
                return false;
        }
    }

    private static bool HasAttribute(
        MetadataReader reader,
        CustomAttributeHandleCollection handles,
        string attributeNamespace,
        string attributeName
    )
    {
        foreach (CustomAttributeHandle handle in handles)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);

            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            MemberReference member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);

            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);

            if (
                reader.GetString(type.Namespace) == attributeNamespace
                && reader.GetString(type.Name) == attributeName
            )
            {
                return true;
            }
        }

        return false;
    }

    // Rendered with the invariant culture and a type tag, so that a decimal separator or a date
    // format cannot make two identical contracts read differently on two machines, and so that
    // 1 and "1" are not the same default.
    private static string DescribeConstant(MetadataReader reader, ConstantHandle handle)
    {
        if (handle.IsNil)
        {
            return "none";
        }

        Constant constant = reader.GetConstant(handle);
        BlobReader blob = reader.GetBlobReader(constant.Value);

        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => "bool:"
                + blob.ReadBoolean().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Char => "char:"
                + ((int)blob.ReadChar()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.SByte => "sbyte:"
                + blob.ReadSByte().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Byte => "byte:"
                + blob.ReadByte().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Int16 => "short:"
                + blob.ReadInt16().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt16 => "ushort:"
                + blob.ReadUInt16().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Int32 => "int:"
                + blob.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt32 => "uint:"
                + blob.ReadUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Int64 => "long:"
                + blob.ReadInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt64 => "ulong:"
                + blob.ReadUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Single => "float:"
                + blob.ReadSingle().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.Double => "double:"
                + blob.ReadDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            ConstantTypeCode.String => "string:" + (blob.ReadUTF16(blob.Length) ?? string.Empty),
            ConstantTypeCode.NullReference => "null",
            _ => "unknown",
        };
    }
}
