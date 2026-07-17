using System.Security.Cryptography;
using System.Text;

namespace RuleCraft.Store;

/// <summary>
/// Hash of the public API shape of the contract and context types. Stored per rule so a
/// contract change between application versions is detectable; rules are recompiled on
/// reload anyway, and quarantined if they no longer compile.
/// </summary>
internal static class ContractFingerprint
{
    public static string Compute(params Type[] types)
    {
        var builder = new StringBuilder();
        foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            builder.Append(type.FullName).Append('\n');
            foreach (var member in type.GetMembers().OrderBy(m => m.ToString(), StringComparer.Ordinal))
                builder.Append("  ").Append(member).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
