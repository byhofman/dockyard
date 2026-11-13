using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Dockyard.Agent;

static class DnsPacket
{
    const int HeaderLength = 12;

    public static DnsQuestion? Question(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= HeaderLength || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1) return null;
        var name = new StringBuilder();
        var i = HeaderLength;
        while (true)
        {
            if (i >= packet.Length) return null;
            int length = packet[i++];
            if (length == 0) break;
            // A question never needs a compression pointer, and a name is at most 255 bytes.
            if (length > 63 || i + length > packet.Length || name.Length > 255) return null;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(packet.Slice(i, length)).ToLowerInvariant());
            i += length;
        }
        if (i + 4 > packet.Length) return null;
        return new DnsQuestion(name.ToString(), BinaryPrimitives.ReadUInt16BigEndian(packet[i..]), i + 4);
    }

    // An address for an A question; other questions about a known name get an empty answer, not an error, so that a
    // client asking AAAA and A at once keeps the A.
    public static byte[] Answer(ReadOnlySpan<byte> query, DnsQuestion question, IPAddress address)
    {
        var hasAnswer = question.Type == DnsQuestion.A;
        var reply = new byte[question.End + (hasAnswer ? 16 : 0)];
        Head(query, question, reply, rcode: 0);
        if (hasAnswer)
        {
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), 1);
            var answer = reply.AsSpan(question.End);
            answer[0] = 0xC0;
            answer[1] = HeaderLength;
            BinaryPrimitives.WriteUInt16BigEndian(answer[2..], DnsQuestion.A);
            BinaryPrimitives.WriteUInt16BigEndian(answer[4..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(answer[6..], 5);
            BinaryPrimitives.WriteUInt16BigEndian(answer[10..], 4);
            address.TryWriteBytes(answer[12..], out _);
        }
        return reply;
    }

    public static byte[] Failure(ReadOnlySpan<byte> query, DnsQuestion question, byte rcode)
    {
        var reply = new byte[question.End];
        Head(query, question, reply, rcode);
        return reply;
    }

    static void Head(ReadOnlySpan<byte> query, DnsQuestion question, Span<byte> reply, byte rcode)
    {
        query[..question.End].CopyTo(reply);
        reply[2] = (byte)(0x80 | (query[2] & 0x01));
        reply[3] = (byte)(0x80 | rcode);
        reply[4] = 0;
        reply[5] = 1;
        reply[6..12].Clear();
    }
}
