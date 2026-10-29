using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace BorderLess.Core;

// Wire codec and channel/context binding around Bouncy Castle's complete
// J-PAKE implementation. No password hashes or PIN verifiers are transmitted.
public static class PinPake
{
    private sealed record Round1(string Id, string Gx1, string Gx2, string[] Proof1, string[] Proof2);
    private sealed record Round2(string Id, string A, string[] Proof);
    private sealed record Round3(string Id, string Tag);
    public static string Normalize(string code)
    {
        code = code.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (code.Length != 8 || code.Any(c => c is < '0' or > '9')) throw new FormatException("Escribe los 8 dígitos que muestra el otro PC.");
        return code;
    }
    private static string Hex(BigInteger n) => n.ToString(16);
    private static BigInteger Number(string text, int maxDigits, bool signed = false)
    {
        if (string.IsNullOrEmpty(text) || text.Length > maxDigits + (signed ? 1 : 0)) throw new InvalidDataException("Valor PAKE fuera de límites.");
        var digits = signed && text[0] == '-' ? text.AsSpan(1) : text.AsSpan();
        if (digits.Length == 0 || digits.Length > maxDigits || digits.ContainsAnyExcept("0123456789abcdef")) throw new InvalidDataException("Valor PAKE inválido.");
        return new BigInteger(text, 16);
    }
    private static BigInteger[] Proof(string[] values)
    {
        if (values == null || values.Length != 2) throw new InvalidDataException("Prueba PAKE inválida.");
        return new[] { Number(values[0], 768), Number(values[1], 64) };
    }
    private static void CheckId(string actual, string expected) { if (!string.Equals(actual, expected, StringComparison.Ordinal)) throw new AuthenticationException("La identidad o el canal de vinculación no coincide."); }
    public static async Task<byte[]> ExchangeAsync(Stream stream, string pin, byte[] contextHash, bool server, CancellationToken ct)
    {
        if (contextHash.Length != 32) throw new ArgumentException("Context hash must be SHA-256.");
        var suffix = Convert.ToHexString(contextHash);
        var own = "BorderLess-PIN/1/" + (server ? "receiver/" : "sender/") + suffix;
        var remote = "BorderLess-PIN/1/" + (server ? "sender/" : "receiver/") + suffix;
        var password = Normalize(pin).ToCharArray();
        var participant = new JPakeParticipant(own, password);
        Array.Clear(password);
        byte[]? material = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var r1 = participant.CreateRound1PayloadToSend();
            await Wire.WriteJsonAsync(stream, new Round1(own, Hex(r1.Gx1), Hex(r1.Gx2), r1.KnowledgeProofForX1.Select(Hex).ToArray(), r1.KnowledgeProofForX2.Select(Hex).ToArray()), ct).ConfigureAwait(false);
            var p1 = await Wire.ReadJsonAsync<Round1>(stream, ct).ConfigureAwait(false); CheckId(p1.Id, remote);
            participant.ValidateRound1PayloadReceived(new(remote, Number(p1.Gx1, 768), Number(p1.Gx2, 768), Proof(p1.Proof1), Proof(p1.Proof2)));
            ct.ThrowIfCancellationRequested();
            var r2 = participant.CreateRound2PayloadToSend();
            await Wire.WriteJsonAsync(stream, new Round2(own, Hex(r2.A), r2.KnowledgeProofForX2s.Select(Hex).ToArray()), ct).ConfigureAwait(false);
            var p2 = await Wire.ReadJsonAsync<Round2>(stream, ct).ConfigureAwait(false); CheckId(p2.Id, remote);
            participant.ValidateRound2PayloadReceived(new(remote, Number(p2.A, 768), Proof(p2.Proof)));
            ct.ThrowIfCancellationRequested();
            var key = participant.CalculateKeyingMaterial();
            var r3 = participant.CreateRound3PayloadToSend(key);
            await Wire.WriteJsonAsync(stream, new Round3(own, Hex(r3.MacTag)), ct).ConfigureAwait(false);
            var p3 = await Wire.ReadJsonAsync<Round3>(stream, ct).ConfigureAwait(false); CheckId(p3.Id, remote);
            participant.ValidateRound3PayloadReceived(new(remote, Number(p3.Tag, 64, signed: true)), key);
            material = key.ToByteArrayUnsigned();
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, contextHash, Encoding.UTF8.GetBytes("BorderLess-PIN/1/persistent-peer-key"));
        }
        catch (CryptoException ex) { throw new AuthenticationException("El código no coincide o la vinculación no pudo verificarse.", ex); }
        finally { if (material != null) CryptographicOperations.ZeroMemory(material); }
    }
}
