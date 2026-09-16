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
// What is emitted is what an implementer outside this assembly can bind to. private protected
// (FamANDAssem) is deliberately absent: it is inaccessible outside the declaring assembly, so a
// change to it cannot break anyone compiling against the package, and failing a publish over it
// would fail over something no consumer can observe.

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
    ImmutableArray<string> methodParameters
)
{
    public ImmutableArray<string> TypeParameters { get; } = typeParameters;

    public ImmutableArray<string> MethodParameters { get; } = methodParameters;

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
/// Emits one assembly's externally consumable surface.
/// </summary>
public static class ContractSurfaceReader
{
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
            GenericContext typeContext = new(typeParameters, ImmutableArray<string>.Empty);

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

            yield return DescribeMethod(reader, provider, method, typeName, typeContext);
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
        MethodDefinition method,
        string typeName,
        GenericContext typeContext
    )
    {
        ImmutableArray<string> methodParameters = GenericParameterNames(
            reader,
            method.GetGenericParameters()
        );
        GenericContext context = new(typeContext.TypeParameters, methodParameters);
        MethodSignature<string> signature = method.DecodeSignature(provider, context);

        List<string> rendered = DescribeSignatureParameters(
            reader,
            signature.ParameterTypes,
            method.GetParameters()
        );

        StringBuilder builder = new();
        builder.Append("METHOD ").Append(typeName).Append('.').Append(reader.GetString(method.Name));
        builder.Append('`').Append(signature.GenericParameterCount);
        builder.Append('(').Append(string.Join(", ", rendered)).Append(')');
        builder.Append(" : ").Append(signature.ReturnType);
        builder.Append(" accessibility=").Append(MemberAccessibility(method.Attributes));
        builder.Append(" modifiers=").Append(MethodModifiers(method.Attributes));
        builder
            .Append(" generics=")
            .Append(DescribeGenericParameters(reader, provider, method.GetGenericParameters(), context));

        return builder.ToString();
    }

    private static string DescribeParameter(MetadataReader reader, string type, Parameter? parameter)
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
                        DescribeDefaultValue(reader, value.GetDefaultValue(), value.GetCustomAttributes())
                    );
            }

            return builder.ToString();
        }

        return type;
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

        builder.Append("FIELD ").Append(typeName).Append('.').Append(reader.GetString(field.Name));
        builder.Append(" : ").Append(field.DecodeSignature(provider, context));
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
        string value = DescribeDefaultValue(reader, field.GetDefaultValue(), field.GetCustomAttributes());

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

        StringBuilder builder = new();
        builder.Append("PROPERTY ").Append(typeName).Append('.').Append(reader.GetString(property.Name));

        if (signature.ParameterTypes.Length > 0)
        {
            // Indexer parameters carry names too, and a named argument binds to them exactly as it
            // does on a method. The names come from whichever accessor exists, since a property row
            // has no parameter rows of its own.
            MethodDefinitionHandle source = accessors.Getter.IsNil ? accessors.Setter : accessors.Getter;

            builder
                .Append('[')
                .Append(
                    string.Join(", ", DescribeSignatureParameters(reader, signature.ParameterTypes, source))
                )
                .Append(']');
        }

        builder.Append(" : ").Append(signature.ReturnType);
        builder.Append(" get=").Append(getter);
        builder.Append(" set=").Append(setter);
        builder.Append(" setkind=").Append(SetterKind(reader, provider, accessors.Setter, context));

        line = builder.ToString();

        return true;
    }

    /// <summary>
    /// Renders one signature's parameters, taking names and modifiers from the owning method when
    /// there is one.
    /// </summary>
    private static List<string> DescribeSignatureParameters(
        MetadataReader reader,
        ImmutableArray<string> parameterTypes,
        MethodDefinitionHandle owner
    ) =>
        DescribeSignatureParameters(
            reader,
            parameterTypes,
            owner.IsNil ? default : reader.GetMethodDefinition(owner).GetParameters()
        );

    private static List<string> DescribeSignatureParameters(
        MetadataReader reader,
        ImmutableArray<string> parameterTypes,
        ParameterHandleCollection owned
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

        List<string> rendered = [];

        for (int index = 0; index < parameterTypes.Length; index++)
        {
            rendered.Add(
                DescribeParameter(
                    reader,
                    parameterTypes[index],
                    parameters.TryGetValue(index + 1, out Parameter parameter) ? parameter : null
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
        builder.Append("EVENT ").Append(typeName).Append('.').Append(reader.GetString(eventDefinition.Name));
        builder.Append(" : ").Append(TypeName(reader, provider, eventDefinition.Type, context));
        builder.Append(" add=").Append(adder);
        builder.Append(" remove=").Append(remover);

        line = builder.ToString();

        return true;
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

            string annotation = NullableAnnotation(reader, parameter);

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

    private static bool IsVisible(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask)
            is MethodAttributes.Public
                or MethodAttributes.Family
                or MethodAttributes.FamORAssem;

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
        ConstantHandle constant,
        CustomAttributeHandleCollection attributes
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
                    return DescribeDecimalConstant(reader, attribute);

                case "DateTimeConstantAttribute":
                    return DescribeDateTimeConstant(reader, attribute);

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
    private static string DescribeDecimalConstant(MetadataReader reader, CustomAttribute attribute)
    {
        BlobReader blob = reader.GetBlobReader(attribute.Value);

        if (blob.Length < 16)
        {
            return "decimal:malformed";
        }

        if (blob.ReadUInt16() != 1)
        {
            return "decimal:malformed";
        }

        byte scale = blob.ReadByte();
        byte sign = blob.ReadByte();
        uint high = blob.ReadUInt32();
        uint middle = blob.ReadUInt32();
        uint low = blob.ReadUInt32();

        decimal value = new(
            unchecked((int)low),
            unchecked((int)middle),
            unchecked((int)high),
            sign != 0,
            scale
        );

        return "decimal:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string DescribeDateTimeConstant(MetadataReader reader, CustomAttribute attribute)
    {
        BlobReader blob = reader.GetBlobReader(attribute.Value);

        if (blob.Length < 10 || blob.ReadUInt16() != 1)
        {
            return "datetime:malformed";
        }

        // Ticks rather than a formatted date: a format would drag in a calendar and a culture, and
        // the tick count is what the compiler recorded.
        return "datetime:" + blob.ReadInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    private static string NullableAnnotation(MetadataReader reader, GenericParameter parameter)
    {
        if (
            !TryReadNullableByte(reader, parameter.GetCustomAttributes(), "NullableAttribute", out byte value)
        )
        {
            EntityHandle parent = parameter.Parent;
            CustomAttributeHandleCollection parentAttributes = parent.Kind switch
            {
                HandleKind.TypeDefinition => reader
                    .GetTypeDefinition((TypeDefinitionHandle)parent)
                    .GetCustomAttributes(),
                HandleKind.MethodDefinition => reader
                    .GetMethodDefinition((MethodDefinitionHandle)parent)
                    .GetCustomAttributes(),
                _ => default,
            };

            if (
                parentAttributes.Count == 0
                || !TryReadNullableByte(reader, parentAttributes, "NullableContextAttribute", out value)
            )
            {
                return string.Empty;
            }
        }

        return value switch
        {
            1 => "notnull",
            _ => "nullable(" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
        };
    }

    private static bool TryReadNullableByte(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        string attributeName,
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

            BlobReader blob = reader.GetBlobReader(attribute.Value);

            // A byte argument is the single-value form. The array form, which annotates a
            // constructed type rather than a bare type parameter, is not a notnull constraint.
            if (blob.Length >= 3 && blob.ReadUInt16() == 1)
            {
                value = blob.ReadByte();

                return true;
            }
        }

        value = 0;

        return false;
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
