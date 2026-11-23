using System.Buffers.Binary;
using System.Text;
using Moderato.Results;
using Mouthtrap.Core;

namespace Mouthtrap.Outputs;

// VRChat native face tracking over OSC: /tracking/face/v1 with one 102-byte blob, one unsigned byte per key in
// VRChat's order, decoded by VRChat as byte / 255. Keys a frame does not provide are sent as 0 in place.
// Source: creators.vrchat.com/avatars/face-tracking/osc/ (updated 2026-10-26).
public static class VrchatFaceOsc
{
    public const string Address = "/tracking/face/v1";
    public const int PayloadLength = 102;
    // Address padded to 20, ",b" padded to 4, int32 blob size, payload padded to 104.
    public const int PacketLength = 132;

    private const int AddressBlock = 20;
    private const int TagBlock = 4;
    private const int PayloadOffset = AddressBlock + TagBlock + 4;

    // The byte order is VRChat's list, written out by name so it never depends on UnifiedExpression's values.
    private static readonly UnifiedExpression[] Order = Checked(
    [
        UnifiedExpression.EyeLookOutRight, UnifiedExpression.EyeLookInRight, UnifiedExpression.EyeLookUpRight,
        UnifiedExpression.EyeLookDownRight, UnifiedExpression.EyeLookOutLeft, UnifiedExpression.EyeLookInLeft,
        UnifiedExpression.EyeLookUpLeft, UnifiedExpression.EyeLookDownLeft, UnifiedExpression.EyeClosedRight,
        UnifiedExpression.EyeClosedLeft, UnifiedExpression.EyeDilationRight, UnifiedExpression.EyeDilationLeft,
        UnifiedExpression.EyeConstrictRight, UnifiedExpression.EyeConstrictLeft, UnifiedExpression.EyeSquintRight,
        UnifiedExpression.EyeSquintLeft, UnifiedExpression.EyeWideRight, UnifiedExpression.EyeWideLeft,
        UnifiedExpression.BrowPinchRight, UnifiedExpression.BrowPinchLeft, UnifiedExpression.BrowLowererRight,
        UnifiedExpression.BrowLowererLeft, UnifiedExpression.BrowInnerUpRight, UnifiedExpression.BrowInnerUpLeft,
        UnifiedExpression.BrowOuterUpRight, UnifiedExpression.BrowOuterUpLeft, UnifiedExpression.NasalDilationRight,
        UnifiedExpression.NasalDilationLeft, UnifiedExpression.NasalConstrictRight,
        UnifiedExpression.NasalConstrictLeft, UnifiedExpression.CheekSquintRight, UnifiedExpression.CheekSquintLeft,
        UnifiedExpression.CheekPuffRight, UnifiedExpression.CheekPuffLeft, UnifiedExpression.CheekSuckRight,
        UnifiedExpression.CheekSuckLeft, UnifiedExpression.JawOpen, UnifiedExpression.JawRight,
        UnifiedExpression.JawLeft, UnifiedExpression.JawForward, UnifiedExpression.JawBackward,
        UnifiedExpression.JawClench, UnifiedExpression.JawMandibleRaise, UnifiedExpression.MouthClosed,
        UnifiedExpression.LipSuckUpperRight, UnifiedExpression.LipSuckUpperLeft, UnifiedExpression.LipSuckLowerRight,
        UnifiedExpression.LipSuckLowerLeft, UnifiedExpression.LipSuckCornerRight, UnifiedExpression.LipSuckCornerLeft,
        UnifiedExpression.LipFunnelUpperRight, UnifiedExpression.LipFunnelUpperLeft,
        UnifiedExpression.LipFunnelLowerRight, UnifiedExpression.LipFunnelLowerLeft,
        UnifiedExpression.LipPuckerUpperRight, UnifiedExpression.LipPuckerUpperLeft,
        UnifiedExpression.LipPuckerLowerRight, UnifiedExpression.LipPuckerLowerLeft,
        UnifiedExpression.MouthUpperUpRight, UnifiedExpression.MouthUpperUpLeft,
        UnifiedExpression.MouthUpperDeepenRight, UnifiedExpression.MouthUpperDeepenLeft,
        UnifiedExpression.NoseSneerRight, UnifiedExpression.NoseSneerLeft, UnifiedExpression.MouthLowerDownRight,
        UnifiedExpression.MouthLowerDownLeft, UnifiedExpression.MouthUpperRight, UnifiedExpression.MouthUpperLeft,
        UnifiedExpression.MouthLowerRight, UnifiedExpression.MouthLowerLeft, UnifiedExpression.MouthCornerPullRight,
        UnifiedExpression.MouthCornerPullLeft, UnifiedExpression.MouthCornerSlantRight,
        UnifiedExpression.MouthCornerSlantLeft, UnifiedExpression.MouthFrownRight, UnifiedExpression.MouthFrownLeft,
        UnifiedExpression.MouthStretchRight, UnifiedExpression.MouthStretchLeft, UnifiedExpression.MouthDimpleRight,
        UnifiedExpression.MouthDimpleLeft, UnifiedExpression.MouthRaiserUpper, UnifiedExpression.MouthRaiserLower,
        UnifiedExpression.MouthPressRight, UnifiedExpression.MouthPressLeft, UnifiedExpression.MouthTightenerRight,
        UnifiedExpression.MouthTightenerLeft, UnifiedExpression.TongueOut, UnifiedExpression.TongueUp,
        UnifiedExpression.TongueDown, UnifiedExpression.TongueRight, UnifiedExpression.TongueLeft,
        UnifiedExpression.TongueRoll, UnifiedExpression.TongueBendDown, UnifiedExpression.TongueCurlUp,
        UnifiedExpression.TongueSquish, UnifiedExpression.TongueFlat, UnifiedExpression.TongueTwistRight,
        UnifiedExpression.TongueTwistLeft, UnifiedExpression.SoftPalateClose, UnifiedExpression.ThroatSwallow,
        UnifiedExpression.NeckFlexRight, UnifiedExpression.NeckFlexLeft
    ]);

    public static UnifiedExpression KeyAt(int offset) => Order[offset];

    // Rounds to the nearest of 0..255; the specification gives VRChat's decoding only.
    public static byte Quantize(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        return (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f, MidpointRounding.AwayFromZero);
    }

    public static void EncodePayload(ExpressionFrame frame, Span<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (payload.Length != PayloadLength) throw new ArgumentException("The payload must be 102 bytes.", nameof(payload));
        for (var offset = 0; offset < PayloadLength; offset++)
            payload[offset] = frame[Order[offset]] is { } value ? Quantize(value) : (byte)0;
    }

    public static void WritePacket(ReadOnlySpan<byte> payload, Span<byte> packet)
    {
        if (payload.Length != PayloadLength) throw new ArgumentException("The payload must be 102 bytes.", nameof(payload));
        if (packet.Length != PacketLength) throw new ArgumentException("The packet must be 132 bytes.", nameof(packet));
        packet.Clear();
        Encoding.ASCII.GetBytes(Address, packet);
        packet[AddressBlock] = (byte)',';
        packet[AddressBlock + 1] = (byte)'b';
        BinaryPrimitives.WriteInt32BigEndian(packet[(AddressBlock + TagBlock)..], PayloadLength);
        payload.CopyTo(packet[PayloadOffset..]);
    }

    // Reads back a packet written by WritePacket; used by tests and the dry run, which shows what would be sent.
    // A packet in another shape is an InvalidValue.
    public static Result<byte[], InvalidValue> ReadPayload(ReadOnlySpan<byte> packet)
    {
        Span<byte> expected = stackalloc byte[PayloadOffset];
        Encoding.ASCII.GetBytes(Address, expected);
        expected[AddressBlock] = (byte)',';
        expected[AddressBlock + 1] = (byte)'b';
        BinaryPrimitives.WriteInt32BigEndian(expected[(AddressBlock + TagBlock)..], PayloadLength);
        if (packet.Length != PacketLength || !packet[..PayloadOffset].SequenceEqual(expected) ||
            packet[(PayloadOffset + PayloadLength)..].ContainsAnyExcept((byte)0))
            return new Err<InvalidValue>(new(nameof(packet), "Not a /tracking/face/v1 packet with a 102-byte blob."));
        return new Ok<byte[]>(packet.Slice(PayloadOffset, PayloadLength).ToArray());
    }

    // The fixed key list must name every key once; a broken list is a bug in this file, not a runtime condition.
    private static UnifiedExpression[] Checked(UnifiedExpression[] order) =>
        order.Length == PayloadLength && order.Distinct().Count() == PayloadLength
            ? order
            : throw new InvalidOperationException("The VRChat key order must list 102 distinct keys.");
}
