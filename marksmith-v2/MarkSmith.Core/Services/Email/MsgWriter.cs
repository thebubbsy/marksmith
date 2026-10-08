using System.Buffers.Binary;
using System.Text;
using MimeKit;
using OpenMcdf;

namespace MarkSmith.Services.Email;

/// <summary>Writes an <see cref="EmailDocument"/> as an Outlook message (.msg, [MS-OXMSG]): a
/// compound file holding the MAPI properties Outlook itself writes for an HTML mail. With
/// <see cref="EmailDocument.IsDraft"/> the message carries <c>MSGFLAG_UNSENT</c>, so Outlook opens
/// it as a compose window you can edit and send, the .msg twin of the .eml's <c>X-Unsent: 1</c>.
///
/// Written by hand on OpenMcdf rather than with MsgKit: MsgKit needs the full MimeKit, whose types
/// collide with the MimeKitLite the rest of the app uses. The file is checked by reading it back
/// with MSGReader, an independent implementation (MsgWriterTests).</summary>
public static class MsgWriter
{
    // Property types ([MS-OXCDATA] 2.11.1).
    private const ushort PtLong = 0x0003, PtBoolean = 0x000B, PtSysTime = 0x0040, PtUnicode = 0x001F, PtBinary = 0x0102;

    // Message flags ([MS-OXCMSG] 2.2.1.6).
    private const int MsgFlagRead = 0x01, MsgFlagUnsent = 0x08, MsgFlagHasAttach = 0x10;

    private const int Utf8CodePage = 65001;

    // The provider UID of a one-off entry ID ([MS-OXCDATA] 2.2.5.1): an SMTP address that isn't in
    // any address book, which is what every recipient of a fresh draft is.
    private static readonly byte[] OneOffProvider =
        { 0x81, 0x2B, 0x1F, 0xA4, 0xBE, 0xA3, 0x10, 0x19, 0x9D, 0x6E, 0x00, 0xDD, 0x01, 0x0F, 0x54, 0x02 };

    public static byte[] ToBytes(EmailDocument doc, DateTime? now = null)
    {
        using var ms = new MemoryStream();
        WriteTo(doc, ms, now);
        return ms.ToArray();
    }

    public static void Write(EmailDocument doc, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp)) WriteTo(doc, fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // A write that failed half way leaves no stray .tmp beside the user's documents.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    public static void WriteTo(EmailDocument doc, Stream output, DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToUniversalTime();
        var recipients = Recipients(doc).ToList();
        var files = doc.InlineImages.Select(i => new Attachment(i.FileName, i.Bytes, i.MimeType, i.ContentId))
            .Concat(doc.Attachments.Select(a => new Attachment(a.FileName, a.Bytes, a.MimeType, null)))
            .ToList();
        var visibleAttachments = doc.Attachments.Count > 0;

        // Build in memory, then copy: OpenMcdf needs a seekable stream it owns while writing.
        using var buffer = new MemoryStream();
        using (var root = RootStorage.Create(buffer, OpenMcdf.Version.V3, StorageModeFlags.LeaveOpen))
        {
            var subject = doc.Subject ?? "";
            var props = new PropertyBag();
            props.Unicode(0x001A, "IPM.Note");                       // PR_MESSAGE_CLASS
            props.Unicode(0x0037, subject);                          // PR_SUBJECT
            props.Unicode(0x003D, "");                               // PR_SUBJECT_PREFIX
            props.Unicode(0x0E1D, subject);                          // PR_NORMALIZED_SUBJECT
            props.Unicode(0x0070, subject);                          // PR_CONVERSATION_TOPIC
            props.Unicode(0x1000, doc.TextBody ?? "");               // PR_BODY
            props.Binary(0x1013, Encoding.UTF8.GetBytes(doc.HtmlBody ?? "")); // PR_HTML
            props.Long(0x1016, 3);                                   // PR_NATIVE_BODY_INFO: HTML
            props.Long(0x3FDE, Utf8CodePage);                        // PR_INTERNET_CPID
            props.Long(0x3FFD, Utf8CodePage);                        // PR_MESSAGE_CODEPAGE
            props.Long(0x0E07, MsgFlagRead | (doc.IsDraft ? MsgFlagUnsent : 0) | (visibleAttachments ? MsgFlagHasAttach : 0)); // PR_MESSAGE_FLAGS
            props.Boolean(0x0E1B, visibleAttachments);               // PR_HASATTACH
            props.Long(0x0017, 1);                                   // PR_IMPORTANCE: normal
            props.Long(0x0026, 0);                                   // PR_PRIORITY: normal
            props.Long(0x0036, 0);                                   // PR_SENSITIVITY: none
            props.Long(0x340D, 0x00040000);                          // PR_STORE_SUPPORT_MASK: STORE_UNICODE_OK
            props.SysTime(0x3007, stamp);                            // PR_CREATION_TIME
            props.SysTime(0x3008, stamp);                            // PR_LAST_MODIFICATION_TIME
            if (!doc.IsDraft)
            {
                props.SysTime(0x0039, stamp);                        // PR_CLIENT_SUBMIT_TIME
                props.SysTime(0x0E06, stamp);                        // PR_MESSAGE_DELIVERY_TIME
            }
            if (!string.IsNullOrWhiteSpace(doc.From) && MailboxAddress.TryParse(doc.From, out var from) && from.Address.Length > 0)
            {
                var name = string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name;
                foreach (var (nameId, typeId, addrId) in new[] { ((ushort)0x0C1A, (ushort)0x0C1E, (ushort)0x0C1F), ((ushort)0x0042, (ushort)0x0064, (ushort)0x0065) })
                {
                    props.Unicode(nameId, name);                     // PR_SENDER_NAME / PR_SENT_REPRESENTING_NAME
                    props.Unicode(typeId, "SMTP");                   // …_ADDRTYPE
                    props.Unicode(addrId, from.Address);             // …_EMAIL_ADDRESS
                }
                props.Unicode(0x5D01, from.Address);                 // PR_SENDER_SMTP_ADDRESS
            }
            props.Unicode(0x0E04, DisplayList(recipients, 1));       // PR_DISPLAY_TO
            props.Unicode(0x0E03, DisplayList(recipients, 2));       // PR_DISPLAY_CC
            props.Unicode(0x0E02, DisplayList(recipients, 3));       // PR_DISPLAY_BCC
            props.WriteTo(root, Header(recipients.Count, files.Count));

            // Named-property mapping: required by Outlook even when the message defines none.
            var nameid = root.CreateStorage("__nameid_version1.0");
            foreach (var s in new[] { "__substg1.0_00020102", "__substg1.0_00030102", "__substg1.0_00040102" })
                nameid.CreateStream(s).Dispose();

            for (var i = 0; i < recipients.Count; i++)
            {
                var r = recipients[i];
                var storage = root.CreateStorage($"__recip_version1.0_#{i:X8}");
                var rp = new PropertyBag();
                rp.Long(0x3000, i);                                  // PR_ROWID
                rp.Long(0x0C15, r.Type);                             // PR_RECIPIENT_TYPE
                rp.Long(0x0FFE, 6);                                  // PR_OBJECT_TYPE: MAPI_MAILUSER
                rp.Long(0x3900, 0);                                  // PR_DISPLAY_TYPE: DT_MAILUSER
                rp.Unicode(0x3001, r.DisplayName);                   // PR_DISPLAY_NAME
                rp.Unicode(0x3A20, r.DisplayName);                   // PR_TRANSMITABLE_DISPLAY_NAME
                rp.Unicode(0x3002, "SMTP");                          // PR_ADDRTYPE
                rp.Unicode(0x3003, r.Address);                       // PR_EMAIL_ADDRESS
                rp.Unicode(0x39FE, r.Address);                       // PR_SMTP_ADDRESS
                var entryId = OneOffEntryId(r.DisplayName, r.Address);
                rp.Binary(0x0FFF, entryId);                          // PR_ENTRYID
                rp.Binary(0x5FF7, entryId);                          // PR_RECIPIENT_ENTRYID
                rp.Binary(0x300B, Encoding.ASCII.GetBytes("SMTP:" + r.Address.ToUpperInvariant() + "\0")); // PR_SEARCH_KEY
                rp.Long(0x5FFD, 1);                                  // PR_RECIPIENT_FLAGS: sendable
                rp.WriteTo(storage, new byte[8]);
            }

            for (var i = 0; i < files.Count; i++)
            {
                var a = files[i];
                var storage = root.CreateStorage($"__attach_version1.0_#{i:X8}");
                var ap = new PropertyBag();
                ap.Long(0x0E21, i);                                  // PR_ATTACH_NUM
                ap.Long(0x0FFE, 7);                                  // PR_OBJECT_TYPE: MAPI_ATTACH
                ap.Long(0x3705, 1);                                  // PR_ATTACH_METHOD: ATTACH_BY_VALUE
                ap.Binary(0x3701, a.Bytes);                          // PR_ATTACH_DATA_BIN
                ap.Long(0x0E20, a.Bytes.Length);                     // PR_ATTACH_SIZE
                ap.Unicode(0x3704, ShortName(a.FileName));           // PR_ATTACH_FILENAME
                ap.Unicode(0x3707, a.FileName);                      // PR_ATTACH_LONG_FILENAME
                ap.Unicode(0x3001, a.FileName);                      // PR_DISPLAY_NAME
                ap.Unicode(0x3703, Path.GetExtension(a.FileName));   // PR_ATTACH_EXTENSION
                ap.Unicode(0x370E, a.MimeType);                      // PR_ATTACH_MIME_TAG
                ap.Long(0x370B, -1);                                 // PR_RENDERING_POSITION: not in the RTF body
                ap.SysTime(0x3007, stamp);
                ap.SysTime(0x3008, stamp);
                if (a.ContentId is { Length: > 0 } cid)
                {
                    // An inline picture the HTML shows as cid:…; hidden so Outlook doesn't also list
                    // it as a paperclip attachment.
                    ap.Unicode(0x3712, cid);                         // PR_ATTACH_CONTENT_ID
                    ap.Long(0x3714, 0x4);                            // PR_ATTACH_FLAGS: ATT_MHTML_REF
                    ap.Boolean(0x7FFE, true);                        // PR_ATTACHMENT_HIDDEN
                }
                ap.WriteTo(storage, new byte[8]);
            }

            root.Flush(true);
        }
        buffer.Position = 0;
        buffer.CopyTo(output);
    }

    private sealed record Attachment(string FileName, byte[] Bytes, string MimeType, string? ContentId);

    private sealed record Recipient(int Type, string DisplayName, string Address);

    private static IEnumerable<Recipient> Recipients(EmailDocument doc)
    {
        foreach (var (list, type) in new[] { (doc.To, 1), (doc.Cc, 2), (doc.Bcc, 3) })
            foreach (var raw in list)
                if (MailboxAddress.TryParse(raw, out var mb) && !string.IsNullOrWhiteSpace(mb.Address))
                    yield return new Recipient(type, string.IsNullOrWhiteSpace(mb.Name) ? mb.Address : mb.Name, mb.Address);
    }

    private static string DisplayList(IEnumerable<Recipient> recipients, int type) =>
        string.Join("; ", recipients.Where(r => r.Type == type).Select(r => r.DisplayName));

    // The top-level property stream's 32-byte header ([MS-OXMSG] 2.4.1.1): reserved, next recipient
    // ID, next attachment ID, recipient count, attachment count, reserved.
    private static byte[] Header(int recipients, int attachments)
    {
        var h = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), recipients);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), attachments);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), recipients);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(20), attachments);
        return h;
    }

    private static byte[] OneOffEntryId(string displayName, string address)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[4]);                    // flags
        ms.Write(OneOffProvider);
        ms.Write(new byte[] { 0x00, 0x00 });      // version
        ms.Write(new byte[] { 0x01, 0x90 });      // MIME, Unicode strings, no rich-info lookup
        foreach (var s in new[] { displayName, "SMTP", address })
        {
            ms.Write(Encoding.Unicode.GetBytes(s));
            ms.Write(new byte[] { 0, 0 });
        }
        return ms.ToArray();
    }

    // The 8.3 PR_ATTACH_FILENAME older clients fall back to.
    private static string ShortName(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        var stem = new string(Path.GetFileNameWithoutExtension(fileName).Where(char.IsLetterOrDigit).Take(8).ToArray());
        if (stem.Length == 0) stem = "file";
        return stem + (ext.Length > 4 ? ext[..4] : ext);
    }

    /// <summary>One object's properties: fixed-size values go in the property stream itself;
    /// strings and binaries go in their own <c>__substg1.0_TTTTPPPP</c> stream ([MS-OXMSG] 2.4).</summary>
    private sealed class PropertyBag
    {
        private readonly List<(ushort Id, ushort Type, byte[] Inline, byte[]? Stream)> _items = new();

        public void Long(ushort id, int value)
        {
            var v = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(v, value);
            _items.Add((id, PtLong, v, null));
        }

        public void Boolean(ushort id, bool value)
        {
            var v = new byte[8];
            v[0] = value ? (byte)1 : (byte)0;
            _items.Add((id, PtBoolean, v, null));
        }

        public void SysTime(ushort id, DateTime utc)
        {
            var v = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(v, utc.ToFileTimeUtc());
            _items.Add((id, PtSysTime, v, null));
        }

        // A string's size counts the terminating null; the stream holds the characters only.
        public void Unicode(ushort id, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value ?? "");
            _items.Add((id, PtUnicode, SizeField(bytes.Length + 2), bytes));
        }

        public void Binary(ushort id, byte[] value) => _items.Add((id, PtBinary, SizeField(value.Length), value));

        private static byte[] SizeField(int size)
        {
            var v = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(v, size);
            return v;
        }

        public void WriteTo(Storage storage, byte[] header)
        {
            using var ms = new MemoryStream();
            ms.Write(header);
            var entry = new byte[16];
            foreach (var (id, type, inline, stream) in _items)
            {
                var tag = ((uint)id << 16) | type;
                BinaryPrimitives.WriteUInt32LittleEndian(entry, tag);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), 0x6); // PROPATTR_READABLE | PROPATTR_WRITABLE
                inline.CopyTo(entry, 8);
                ms.Write(entry);
                if (stream is not null)
                {
                    using var s = storage.CreateStream($"__substg1.0_{tag:X8}");
                    s.Write(stream);
                }
            }
            using var props = storage.CreateStream("__properties_version1.0");
            props.Write(ms.ToArray());
        }
    }
}
