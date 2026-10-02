// abicheck <plugin.dll> <dir-with-jellyfin-assemblies> [--prefix Jellyfin. --prefix MediaBrowser.]
// Verifies that every member the plugin references in Jellyfin assemblies exists, with an identical signature,
// in the given Jellyfin version. Exit code 1 when anything is missing or changed.
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2) { Console.Error.WriteLine("usage: abicheck <plugin.dll> <jellyfin-dir>"); return 2; }
var prefixes = new[] { "Jellyfin.", "MediaBrowser.", "Emby." };

var targets = new Dictionary<string, (MetadataReader r, TypeDefinitionHandle h)>();
var readers = new List<(PEReader pe, MetadataReader r)>();
foreach (var dll in Directory.GetFiles(args[1], "*.dll"))
{
    try
    {
        var pe = new PEReader(File.OpenRead(dll));
        if (!pe.HasMetadata) continue;
        var r = pe.GetMetadataReader();
        if (!r.IsAssembly) continue;
        var an = r.GetString(r.GetAssemblyDefinition().Name);
        if (!prefixes.Any(p => an.StartsWith(p))) { pe.Dispose(); continue; }
        readers.Add((pe, r));
        foreach (var th in r.TypeDefinitions)
            targets.TryAdd(DefName(r, th), (r, th));
    }
    catch (BadImageFormatException) { }
}

using var plugin = new PEReader(File.OpenRead(args[0]));
var pr = plugin.GetMetadataReader();
int problems = 0, checkedCount = 0;
var seen = new HashSet<string>();

foreach (var mh in pr.MemberReferences)
{
    var m = pr.GetMemberReference(mh);
    var owner = OwnerName(pr, m.Parent, out var isJellyfin);
    if (owner is null || !isJellyfin) continue;
    var name = pr.GetString(m.Name);
    var sig = m.GetKind() == MemberReferenceKind.Method
        ? MethodSig(pr, m.Signature) : FieldSig(pr, m.Signature);
    var key = owner + "::" + name + sig;
    if (!seen.Add(key)) continue;
    checkedCount++;
    if (!targets.ContainsKey(owner)) { Report($"MISSING TYPE {owner} (needed for {name})"); continue; }
    if (!FindMember(owner, name, sig, m.GetKind() == MemberReferenceKind.Method, new HashSet<string>()))
        Report($"MISSING/CHANGED MEMBER {owner}::{name} {sig}");
}

// Also verify plugin type refs themselves (base classes, interfaces, attributes).
foreach (var th in pr.TypeReferences)
{
    var tr = pr.GetTypeReference(th);
    var n = RefName(pr, th, out var jf);
    if (jf && n is not null && seen.Add("T:" + n) && !targets.ContainsKey(n))
        Report($"MISSING TYPE {n}");
}

Console.WriteLine($"abicheck: {checkedCount} Jellyfin member references checked, {problems} problem(s).");
return problems == 0 ? 0 : 1;

void Report(string s) { problems++; Console.WriteLine("  " + s); }

bool FindMember(string type, string name, string sig, bool isMethod, HashSet<string> visited)
{
    if (!visited.Add(type) || !targets.TryGetValue(type, out var t)) return false;
    var (r, h) = t;
    var td = r.GetTypeDefinition(h);
    if (isMethod)
    {
        foreach (var mh in td.GetMethods())
        {
            var md = r.GetMethodDefinition(mh);
            if (r.GetString(md.Name) == name && MethodSig(r, md.Signature) == sig) return true;
        }
    }
    else
    {
        foreach (var fh in td.GetFields())
        {
            var fd = r.GetFieldDefinition(fh);
            if (r.GetString(fd.Name) == name && FieldSig(r, fd.Signature) == sig) return true;
        }
    }
    if (!td.BaseType.IsNil && ParentName(r, td.BaseType) is { } bn && FindMember(bn, name, sig, isMethod, visited)) return true;
    foreach (var ih in td.GetInterfaceImplementations())
        if (ParentName(r, r.GetInterfaceImplementation(ih).Interface) is { } iname && FindMember(iname, name, sig, isMethod, visited)) return true;
    return false;
}

string? ParentName(MetadataReader r, EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.TypeDefinition: return DefName(r, (TypeDefinitionHandle)h);
        case HandleKind.TypeReference: return RefName(r, (TypeReferenceHandle)h, out _);
        case HandleKind.TypeSpecification:
            var blob = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
            var code = blob.ReadByte();
            if (code == 0x15) // GENERICINST
            {
                blob.ReadByte();
                var eh = blob.ReadTypeHandle();
                return ParentName(r, eh);
            }
            return null;
    }
    return null;
}

string? OwnerName(MetadataReader r, EntityHandle parent, out bool isJellyfin)
{
    isJellyfin = false;
    if (parent.Kind == HandleKind.TypeReference) return RefName(r, (TypeReferenceHandle)parent, out isJellyfin);
    if (parent.Kind == HandleKind.TypeSpecification)
    {
        var n = ParentName(r, parent);
        if (n is null) return null;
        // generic instantiation of a type ref: determine ownership by walking the type ref
        var blob = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
        if (blob.ReadByte() == 0x15)
        {
            blob.ReadByte();
            var eh = blob.ReadTypeHandle();
            if (eh.Kind == HandleKind.TypeReference) { RefName(r, (TypeReferenceHandle)eh, out isJellyfin); }
        }
        return n;
    }
    return null;
}

string RefName(MetadataReader r, TypeReferenceHandle h, out bool isJellyfin)
{
    var tr = r.GetTypeReference(h);
    var nm = r.GetString(tr.Name);
    isJellyfin = false;
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
    {
        var outer = RefName(r, (TypeReferenceHandle)tr.ResolutionScope, out isJellyfin);
        return outer + "+" + nm;
    }
    if (tr.ResolutionScope.Kind == HandleKind.AssemblyReference)
    {
        var an = r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
        isJellyfin = prefixes.Any(p => an.StartsWith(p));
    }
    var ns = r.GetString(tr.Namespace);
    return ns.Length == 0 ? nm : ns + "." + nm;
}

string DefName(MetadataReader r, TypeDefinitionHandle h)
{
    var td = r.GetTypeDefinition(h);
    var nm = r.GetString(td.Name);
    var decl = td.GetDeclaringType();
    if (!decl.IsNil) return DefName(r, decl) + "+" + nm;
    var ns = r.GetString(td.Namespace);
    return ns.Length == 0 ? nm : ns + "." + nm;
}

string MethodSig(MetadataReader r, BlobHandle b) => Decode(r, r.GetBlobReader(b));
string FieldSig(MetadataReader r, BlobHandle b) => Decode(r, r.GetBlobReader(b));

// Minimal signature decoder -> canonical string (type identity by full name; ignores assembly).
string Decode(MetadataReader r, BlobReader blob)
{
    var sb = new System.Text.StringBuilder();
    var hdr = blob.ReadByte();
    var kind = hdr & 0x0F;
    if (kind == 0x06) { sb.Append("F:"); Type(r, ref blob, sb); return sb.ToString(); }
    bool generic = (hdr & 0x10) != 0;
    bool hasThis = (hdr & 0x20) != 0;
    int gcount = generic ? blob.ReadCompressedInteger() : 0;
    int pc = blob.ReadCompressedInteger();
    sb.Append(hasThis ? "i" : "s").Append(gcount > 0 ? $"<{gcount}>" : "").Append('(');
    var ret = new System.Text.StringBuilder();
    Type(r, ref blob, ret);
    for (int i = 0; i < pc; i++)
    {
        if (i > 0) sb.Append(',');
        Type(r, ref blob, sb);
    }
    sb.Append(")->").Append(ret);
    return sb.ToString();
}

void Type(MetadataReader r, ref BlobReader blob, System.Text.StringBuilder sb)
{
    var code = blob.ReadCompressedInteger();
    switch (code)
    {
        case 0x01: sb.Append("void"); break;
        case 0x02: sb.Append("bool"); break;
        case 0x03: sb.Append("char"); break;
        case 0x04: sb.Append("i1"); break;
        case 0x05: sb.Append("u1"); break;
        case 0x06: sb.Append("i2"); break;
        case 0x07: sb.Append("u2"); break;
        case 0x08: sb.Append("i4"); break;
        case 0x09: sb.Append("u4"); break;
        case 0x0A: sb.Append("i8"); break;
        case 0x0B: sb.Append("u8"); break;
        case 0x0C: sb.Append("r4"); break;
        case 0x0D: sb.Append("r8"); break;
        case 0x0E: sb.Append("string"); break;
        case 0x0F: Type(r, ref blob, sb); sb.Append('*'); break;
        case 0x10: Type(r, ref blob, sb); sb.Append('&'); break;
        case 0x11: case 0x12:
            var h = blob.ReadTypeHandle();
            sb.Append(ParentName(r, h) ?? "?");
            break;
        case 0x13: sb.Append('!').Append(blob.ReadCompressedInteger()); break;       // VAR
        case 0x1E: sb.Append("!!").Append(blob.ReadCompressedInteger()); break;      // MVAR
        case 0x14:                                                                     // ARRAY
            Type(r, ref blob, sb);
            var rank = blob.ReadCompressedInteger();
            var ns = blob.ReadCompressedInteger(); for (int i = 0; i < ns; i++) blob.ReadCompressedInteger();
            var nl = blob.ReadCompressedInteger(); for (int i = 0; i < nl; i++) blob.ReadCompressedInteger();
            sb.Append("[").Append(',', rank - 1).Append(']');
            break;
        case 0x15:                                                                     // GENERICINST
            blob.ReadByte();
            sb.Append(ParentName(r, blob.ReadTypeHandle()) ?? "?").Append('<');
            var n = blob.ReadCompressedInteger();
            for (int i = 0; i < n; i++) { if (i > 0) sb.Append(','); Type(r, ref blob, sb); }
            sb.Append('>');
            break;
        case 0x16: sb.Append("typedref"); break;
        case 0x18: sb.Append("iptr"); break;
        case 0x19: sb.Append("uptr"); break;
        case 0x1C: sb.Append("object"); break;
        case 0x1D: Type(r, ref blob, sb); sb.Append("[]"); break;                    // SZARRAY
        case 0x1B: sb.Append("fnptr"); break;
        case 0x1F: case 0x20:                                                          // CMOD_REQD / CMOD_OPT
            blob.ReadTypeHandle(); Type(r, ref blob, sb); break;
        case 0x45: sb.Append("pinned "); Type(r, ref blob, sb); break;
        default: sb.Append($"?{code:X}"); break;
    }
}
