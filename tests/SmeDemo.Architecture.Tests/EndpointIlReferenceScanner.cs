using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SmeDemo.Architecture.Tests;

/// <summary>
/// Reads the built API assembly with <see cref="MetadataReader"/> and reports
/// every type, field, property, method signature, base type, interface and IL
/// member reference made by a type in <c>SmeDemo.Api.Endpoints</c> that points at
/// EF Core, an Azure SDK type or <c>System.Net.Http</c>.
/// </summary>
internal sealed class EndpointIlReferenceScanner
{
    private const string EndpointNamespace = "SmeDemo.Api.Endpoints";

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = BuildOpCodeMap();

    private readonly PEReader _peReader;
    private readonly MetadataReader _reader;
    private readonly HashSet<string> _findings = new(StringComparer.Ordinal);

    public EndpointIlReferenceScanner(PEReader peReader)
    {
        _peReader = peReader;
        _reader = peReader.GetMetadataReader();
    }

    public IReadOnlyCollection<string> FindForbiddenReferences()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            if (!IsEndpointType(typeHandle))
            {
                continue;
            }

            var typeName = GetTypeDefinitionDisplayName(typeHandle);
            var typeDefinition = _reader.GetTypeDefinition(typeHandle);

            ScanTypeShape(typeName, typeDefinition);
            ScanFields(typeName, typeDefinition);
            ScanProperties(typeName, typeDefinition);
            ScanMethods(typeName, typeDefinition);
        }

        return _findings;
    }

    private void ScanTypeShape(string typeName, TypeDefinition typeDefinition)
    {
        if (!typeDefinition.BaseType.IsNil)
        {
            RecordEntityType(typeDefinition.BaseType, $"base type of {typeName}");
        }

        foreach (var implementationHandle in typeDefinition.GetInterfaceImplementations())
        {
            var implementation = _reader.GetInterfaceImplementation(implementationHandle);
            RecordEntityType(implementation.Interface, $"interface of {typeName}");
        }
    }

    private void ScanFields(string typeName, TypeDefinition typeDefinition)
    {
        foreach (var fieldHandle in typeDefinition.GetFields())
        {
            var field = _reader.GetFieldDefinition(fieldHandle);
            var context = $"field {typeName}.{_reader.GetString(field.Name)}";
            _ = field.DecodeSignature(new RecordingTypeProvider(this, context), genericContext: null);
        }
    }

    private void ScanProperties(string typeName, TypeDefinition typeDefinition)
    {
        foreach (var propertyHandle in typeDefinition.GetProperties())
        {
            var property = _reader.GetPropertyDefinition(propertyHandle);
            var context = $"property {typeName}.{_reader.GetString(property.Name)}";
            _ = property.DecodeSignature(new RecordingTypeProvider(this, context), genericContext: null);
        }
    }

    private void ScanMethods(string typeName, TypeDefinition typeDefinition)
    {
        foreach (var methodHandle in typeDefinition.GetMethods())
        {
            var method = _reader.GetMethodDefinition(methodHandle);
            var methodName = $"{typeName}.{_reader.GetString(method.Name)}";
            var context = $"method {methodName}";

            _ = method.DecodeSignature(new RecordingTypeProvider(this, context), genericContext: null);

            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = _peReader.GetMethodBody(method.RelativeVirtualAddress);

            if (!body.LocalSignature.IsNil)
            {
                var localSignature = _reader.GetStandaloneSignature(body.LocalSignature);
                _ = localSignature.DecodeLocalSignature(
                    new RecordingTypeProvider(this, $"{context} locals"),
                    genericContext: null);
            }

            foreach (var region in body.ExceptionRegions)
            {
                if (region.Kind == ExceptionRegionKind.Catch && !region.CatchType.IsNil)
                {
                    RecordEntityType(region.CatchType, $"{context} catch clause");
                }
            }

            ScanIl(body.GetILReader(), context);
        }
    }

    private void ScanIl(BlobReader il, string context)
    {
        while (il.RemainingBytes > 0)
        {
            var firstByte = il.ReadByte();
            var value = firstByte == 0xFE
                ? (ushort)(0xFE00 | il.ReadByte())
                : firstByte;

            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                throw new InvalidOperationException(
                    $"Unknown IL opcode 0x{value:X4} while scanning {context}.");
            }

            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineI:
                    _ = il.ReadByte();
                    break;
                case OperandType.InlineI:
                    _ = il.ReadInt32();
                    break;
                case OperandType.InlineI8:
                    _ = il.ReadInt64();
                    break;
                case OperandType.ShortInlineR:
                    _ = il.ReadSingle();
                    break;
                case OperandType.InlineR:
                    _ = il.ReadDouble();
                    break;
                case OperandType.ShortInlineBrTarget:
                    _ = il.ReadByte();
                    break;
                case OperandType.InlineBrTarget:
                    _ = il.ReadInt32();
                    break;
                case OperandType.InlineSwitch:
                    var switchCount = il.ReadInt32();
                    if (switchCount < 0)
                    {
                        throw new InvalidOperationException(
                            $"Negative switch count while scanning {context}.");
                    }

                    _ = il.ReadBytes(switchCount * 4);
                    break;
                case OperandType.InlineString:
                    _ = il.ReadInt32();
                    break;
                case OperandType.InlineSig:
                    _ = il.ReadInt32();
                    break;
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    ProcessToken(il.ReadInt32(), context);
                    break;
                case OperandType.InlineVar:
                    _ = il.ReadUInt16();
                    break;
                case OperandType.ShortInlineVar:
                    _ = il.ReadByte();
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported IL operand type {opCode.OperandType} for {opCode.Name} " +
                        $"while scanning {context}.");
            }
        }
    }

    private void ProcessToken(int token, string context)
    {
        if (token == 0)
        {
            return;
        }

        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (ArgumentException)
        {
            return;
        }

        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                ScanMemberReference((MemberReferenceHandle)handle, context);
                break;
            case HandleKind.MethodDefinition:
                RecordTypeDefinition(
                    _reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType(),
                    context);
                break;
            case HandleKind.FieldDefinition:
                RecordTypeDefinition(
                    _reader.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType(),
                    context);
                break;
            case HandleKind.TypeDefinition:
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
                RecordEntityType(handle, context);
                break;
            case HandleKind.StandaloneSignature:
                break;
            default:
                break;
        }
    }

    private void ScanMemberReference(MemberReferenceHandle handle, string context)
    {
        var reference = _reader.GetMemberReference(handle);
        var memberName = _reader.GetString(reference.Name);
        var memberContext = $"{context} -> {memberName}";

        RecordEntityType(reference.Parent, memberContext);

        var provider = new RecordingTypeProvider(this, memberContext);
        if (reference.GetKind() == MemberReferenceKind.Method)
        {
            _ = reference.DecodeMethodSignature(provider, genericContext: null);
        }
        else
        {
            _ = reference.DecodeFieldSignature(provider, genericContext: null);
        }
    }

    private void RecordEntityType(EntityHandle handle, string context)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                RecordTypeDefinition((TypeDefinitionHandle)handle, context);
                break;
            case HandleKind.TypeReference:
                RecordTypeReference((TypeReferenceHandle)handle, context);
                break;
            case HandleKind.TypeSpecification:
                RecordTypeSpecification((TypeSpecificationHandle)handle, context);
                break;
            case HandleKind.MethodDefinition:
                RecordTypeDefinition(
                    _reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType(),
                    context);
                break;
            default:
                break;
        }
    }

    private string RecordTypeDefinition(TypeDefinitionHandle handle, string context)
    {
        var displayName = GetTypeDefinitionDisplayName(handle);
        Record(GetTypeDefinitionNamespace(handle), displayName, context);
        return displayName;
    }

    private string RecordTypeReference(TypeReferenceHandle handle, string context)
    {
        var reference = _reader.GetTypeReference(handle);
        var displayName = GetTypeReferenceDisplayName(reference);
        Record(GetTypeReferenceNamespace(reference), displayName, context);
        return displayName;
    }

    private string RecordTypeSpecification(TypeSpecificationHandle handle, string context)
    {
        var specification = _reader.GetTypeSpecification(handle);
        return specification.DecodeSignature(
            new RecordingTypeProvider(this, context),
            genericContext: null);
    }

    private void Record(string @namespace, string displayName, string context)
    {
        if (IsForbiddenNamespace(@namespace))
        {
            _findings.Add($"{displayName} ({context})");
        }
    }

    private static bool IsForbiddenNamespace(string @namespace) =>
        @namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
        @namespace.StartsWith("Azure.", StringComparison.Ordinal) ||
        string.Equals(@namespace, "System.Net.Http", StringComparison.Ordinal) ||
        @namespace.StartsWith("System.Net.Http.", StringComparison.Ordinal);

    private bool IsEndpointType(TypeDefinitionHandle handle)
    {
        var typeDefinition = _reader.GetTypeDefinition(handle);
        var declaringType = typeDefinition.GetDeclaringType();
        while (!declaringType.IsNil)
        {
            typeDefinition = _reader.GetTypeDefinition(declaringType);
            declaringType = typeDefinition.GetDeclaringType();
        }

        var @namespace = _reader.GetString(typeDefinition.Namespace);
        return string.Equals(@namespace, EndpointNamespace, StringComparison.Ordinal) ||
               @namespace.StartsWith(EndpointNamespace + ".", StringComparison.Ordinal);
    }

    private string GetTypeDefinitionNamespace(TypeDefinitionHandle handle)
    {
        var typeDefinition = _reader.GetTypeDefinition(handle);
        var declaringType = typeDefinition.GetDeclaringType();
        while (!declaringType.IsNil)
        {
            typeDefinition = _reader.GetTypeDefinition(declaringType);
            declaringType = typeDefinition.GetDeclaringType();
        }

        return _reader.GetString(typeDefinition.Namespace);
    }

    private string GetTypeDefinitionDisplayName(TypeDefinitionHandle handle)
    {
        var typeDefinition = _reader.GetTypeDefinition(handle);
        var name = _reader.GetString(typeDefinition.Name);
        var declaringType = typeDefinition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return $"{GetTypeDefinitionDisplayName(declaringType)}+{name}";
        }

        var @namespace = _reader.GetString(typeDefinition.Namespace);
        return @namespace.Length == 0 ? name : $"{@namespace}.{name}";
    }

    private string GetTypeReferenceNamespace(TypeReference reference)
    {
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return GetTypeReferenceNamespace(
                _reader.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope));
        }

        return _reader.GetString(reference.Namespace);
    }

    private string GetTypeReferenceDisplayName(TypeReference reference)
    {
        var name = _reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{GetTypeReferenceDisplayName(_reader.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope))}+{name}";
        }

        var @namespace = _reader.GetString(reference.Namespace);
        return @namespace.Length == 0 ? name : $"{@namespace}.{name}";
    }

    private static Dictionary<ushort, OpCode> BuildOpCodeMap()
    {
        var map = new Dictionary<ushort, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode) || field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            map.TryAdd(unchecked((ushort)opCode.Value), opCode);
        }

        return map;
    }

    private sealed class RecordingTypeProvider : ISignatureTypeProvider<string, object?>
    {
        private readonly EndpointIlReferenceScanner _scanner;
        private readonly string _context;

        public RecordingTypeProvider(EndpointIlReferenceScanner scanner, string context)
        {
            _scanner = scanner;
            _context = context;
        }

        public string GetArrayType(string elementType, ArrayShape shape) => elementType;

        public string GetByReferenceType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "function pointer";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => _scanner.RecordTypeDefinition(handle, _context);

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => _scanner.RecordTypeReference(handle, _context);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => _scanner.RecordTypeSpecification(handle, _context);
    }
}
