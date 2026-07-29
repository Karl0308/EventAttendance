using System.Buffers.Text;
using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The wire form of the D-30 live cursor. Pure arithmetic over eight bytes, so it belongs in the unit
/// suite — and it is worth pinning at all because every failure mode of a cursor is silent: a client
/// simply stops seeing rows, which looks exactly like an event where nothing is happening.
/// </summary>
public class AttendanceCursorTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(2001L)]
    [InlineData(long.MaxValue)]
    public void A_cursor_round_trips(long rowVersion)
    {
        Assert.True(AttendanceCursor.TryDecode(AttendanceCursor.Encode(rowVersion), out var decoded));
        Assert.Equal(rowVersion, decoded);
    }

    /// <summary>
    /// Eight bytes as base64url: eleven characters, no padding. Asserted because a client stores this
    /// string and a length that moved would be a wire change nobody reviewed.
    /// </summary>
    [Fact]
    public void A_cursor_is_eleven_unpadded_characters()
    {
        var encoded = AttendanceCursor.Encode(2001);

        Assert.Equal(11, encoded.Length);
        Assert.DoesNotContain('=', encoded);
    }

    /// <summary>
    /// <b>No cursor this API issues can contain a character a URL treats specially.</b> The Phase 4d
    /// review's finding, and the failure it prevents is both silent and permanent.
    ///
    /// <para>
    /// Standard base64's alphabet includes <c>+</c>, which an unencoded query string decodes as a space;
    /// <c>Convert.TryFromBase64String</c> then <em>ignores</em> that whitespace, so eleven data
    /// characters decode to six bytes instead of eight and the poll returns <c>400 InvalidCursor</c>.
    /// The client falls back to a full snapshot — and every cursor it receives afterwards has the same
    /// problem, so it storms snapshots and never recovers.
    /// </para>
    ///
    /// <para>
    /// The scan is over the row-version range where character nine (<c>byte6 &gt;&gt; 2</c>) reaches
    /// alphabet index 62, which is <c>+</c> under standard base64 and <c>-</c> under base64url. Under
    /// the old encoding this loop failed on the first value it reached; it is written as a loop rather
    /// than as one known-bad constant so it keeps covering the boundary if the encoding is ever touched
    /// again.
    /// </para>
    /// </summary>
    [Fact]
    public void No_issued_cursor_contains_a_url_significant_character()
    {
        // byte6 lands in 248..251 across this range, which is where index 62 is emitted.
        for (var rowVersion = 0xF8L << 8; rowVersion <= (0xFBL << 8) + 0xFF; rowVersion++)
        {
            var encoded = AttendanceCursor.Encode(rowVersion);

            Assert.True(
                encoded.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
                $"Encode({rowVersion}) produced '{encoded}', which is not base64url. A '+' in a " +
                "cursor is decoded as a space by any URL parser, the decode then yields six bytes " +
                "instead of eight, and the client is told its cursor is invalid — forever, because " +
                "every cursor it is handed next has the same problem.");
        }
    }

    /// <summary>
    /// A cursor issued before the encoding narrowed still decodes. Cutting over would have handed the
    /// client mid-poll exactly the snapshot storm the change exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(2001L)]
    [InlineData(0xFB00L)]
    public void A_standard_base64_cursor_from_before_the_change_still_decodes(long rowVersion)
    {
        var legacy = Convert.ToBase64String(
            [.. BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(rowVersion))]);

        Assert.True(AttendanceCursor.TryDecode(legacy, out var decoded));
        Assert.Equal(rowVersion, decoded);
    }

    /// <summary>
    /// <b>The property the whole cursor depends on.</b> SQL Server compares <c>binary(8)</c> left to
    /// right, so the encoding has to be the one where byte order and numeric order agree — big-endian.
    /// Little-endian would round-trip perfectly and produce a cursor that sorts wrongly in the database,
    /// which is the exact class of failure nothing else here would catch: 256 would appear to precede 1.
    /// </summary>
    [Fact]
    public void A_larger_row_version_encodes_to_a_larger_byte_sequence()
    {
        // Decoded with the encoding under test rather than through TryDecode, which returns a long and
        // would therefore assert nothing about the byte order this test exists for.
        var smaller = Base64Url.DecodeFromChars(AttendanceCursor.Encode(1));
        var larger = Base64Url.DecodeFromChars(AttendanceCursor.Encode(256));

        Assert.True(
            Compare(smaller, larger) < 0,
            "Encode(1) does not sort before Encode(256) under byte-wise comparison. The encoding is " +
            "not big-endian, so the CLR ordering and SQL Server's binary(8) ordering disagree — a " +
            "delta query would return rows the cursor has already passed and skip rows it has not.");

        static int Compare(byte[] a, byte[] b)
        {
            for (var i = 0; i < a.Length; i++)
            {
                var d = a[i].CompareTo(b[i]);
                if (d != 0) return d;
            }

            return 0;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")]                    // decodes, but to three bytes.
    [InlineData("AAAAAAAAAAAAAAAAAAAA")]    // decodes, but to fifteen.
    [InlineData("AAAAAAAA wA=")]            // the '+'-became-a-space case: right length, six bytes.
    [InlineData("AAAAAAAA*wA=")]            // right length, character outside both alphabets.
    public void Anything_that_is_not_eight_base64_bytes_is_refused(string? cursor)
    {
        Assert.False(AttendanceCursor.TryDecode(cursor, out var decoded));
        Assert.Equal(AttendanceCursor.Beginning, decoded);
    }

    /// <summary>
    /// <b>No input reaches <see cref="AttendanceCursor.TryDecode"/> that can make it throw</b> — it is
    /// fed straight from a query string.
    ///
    /// <para>
    /// Written because the first implementation did throw. <c>Base64Url.TryDecodeFromChars</c> raises
    /// <c>FormatException</c> for a character outside its alphabet, despite the <c>Try</c> prefix and
    /// the <c>bool</c> return, so "try base64url, fall back to standard base64" turned every legacy
    /// cursor containing a <c>+</c> into a 500 on the endpoint whose entire purpose here was to stop
    /// returning a 400.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("+++++++++++=")]
    [InlineData("////////////")]
    [InlineData("AAAAAAAA+wA=")]
    [InlineData("////===/////")]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0")]
    [InlineData("日本語日本語日本語日本")]
    public void No_query_string_can_make_the_decoder_throw(string cursor) =>
        Assert.Null(Record.Exception(() => AttendanceCursor.TryDecode(cursor, out _)));
}
