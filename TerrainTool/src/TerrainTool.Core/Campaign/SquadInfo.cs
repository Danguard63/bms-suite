using System.Text;

namespace TerrainTool.Core.Campaign;

/// <summary>
/// Lista degli squadroni nel .cmp (SquadInfo, dal codice di Mission Commander: CreateByteArray.cs), dopo la mappa della campagna:
/// u16 numero, poi per squadrone X, Y (float, piedi: X = nord, Y = est), VU_ID, tipo CT, numero, icona della base, stemma, specialita',
/// aerei, paese, nome della base (80 car.), padding, flag, CampId, texSet, nome (80 car.) = 196 byte (versione 102 e oltre).
/// In Korea elenca tutti gli squadroni; la si tiene allineata quando si aggiunge, toglie, sposta o cambia uno squadrone.
/// </summary>
public static class SquadInfo
{
    public const int Size = 196;
    public const double FeetPerKm = 3280.84;

    /// <summary>Posizione del contatore (u16) della lista, o -1 se non trovata (file prima della versione 102 o lista diversa).</summary>
    public static int Locate(CampaignDoc d)
    {
        var c = d.CmpRaw;
        if (c == null || d.Version < 102 || d.Units == null) return -1;
        var ids = d.Units.Units.Where(u => u.Kind == UnitKind.Squadron).Select(u => u.Id).ToHashSet();
        // di solito finisce 13 byte prima della fine (tempo, creatore, data...): si prova prima li', poi si cerca
        for (int tail = 13; tail <= 64; tail++)
        {
            int end = c.Length - tail;
            for (int n = 0; n <= Math.Max(ids.Count + 64, 1); n++)
            {
                int at = end - n * Size - 2;
                if (at < 0) break;
                if (BitConverter.ToUInt16(c, at) != n) continue;
                if (n == 0 && ids.Count > 0) continue;
                bool ok = true;
                for (int i = 0; i < n && ok; i++) ok = ids.Contains(VuId.Read(c, at + 2 + i * Size + 8));
                if (ok) return at;
            }
        }
        return -1;
    }

    public static int Count(CampaignDoc d, int at) => BitConverter.ToUInt16(d.CmpRaw!, at);

    public static int Find(CampaignDoc d, int at, VuId id)
    {
        int n = Count(d, at);
        for (int i = 0; i < n; i++) if (VuId.Read(d.CmpRaw!, at + 2 + i * Size + 8) == id) return at + 2 + i * Size;
        return -1;
    }

    private static void Str(byte[] b, int o, string s, int len)
    {
        Array.Clear(b, o, len);
        var x = Encoding.Latin1.GetBytes(s);
        Array.Copy(x, 0, b, o, Math.Min(len - 1, x.Length));
    }

    /// <summary>Scrive i campi dell'elemento dello squadrone (lo aggiunge in testa se non c'e').</summary>
    public static bool Upsert(CampaignDoc d, CampaignDb db, CampUnit u)
    {
        int at = Locate(d);
        if (at < 0) return false;
        int e = Find(d, at, u.Id);
        if (e < 0)
        {
            var c = d.CmpRaw!;
            int n = Count(d, at);
            var nc = new byte[c.Length + Size];
            Array.Copy(c, 0, nc, 0, at + 2);
            Array.Copy(c, at + 2, nc, at + 2 + Size, c.Length - at - 2);   // il nuovo in testa, come i piu' recenti in Korea
            BitConverter.TryWriteBytes(nc.AsSpan(at, 2), (ushort)(n + 1));
            d.CmpRaw = nc;
            e = at + 2;
        }
        var b = d.CmpRaw!;
        double north, east; string baseName; int icon = 0;
        if (d.Obj(u.Airbase) is { } o)
        {
            north = o.HasSim ? o.SimNorth : CampaignDoc.GridToSim(o.Y); east = o.HasSim ? o.SimEast : CampaignDoc.GridToSim(o.X);
            baseName = o.Name; icon = db.ObjectiveIcon(o.Type - 100);
        }
        else
        {
            north = CampaignDoc.GridToSim(u.Y); east = CampaignDoc.GridToSim(u.X);
            baseName = d.Unit(u.Airbase) is { } tf ? db.UnitVehicles(tf.Type - 100).FirstOrDefault() ?? "" : ReadName(b, e + 27) is { Length: > 0 } old ? old : "Nowhere";
        }
        BitConverter.TryWriteBytes(b.AsSpan(e, 4), (float)north);
        BitConverter.TryWriteBytes(b.AsSpan(e + 4, 4), (float)east);
        u.Id.Write(b, e + 8);
        BitConverter.TryWriteBytes(b.AsSpan(e + 16, 2), (short)(u.Type - 100));
        BitConverter.TryWriteBytes(b.AsSpan(e + 18, 2), u.NameId);
        BitConverter.TryWriteBytes(b.AsSpan(e + 20, 2), (short)icon);
        BitConverter.TryWriteBytes(b.AsSpan(e + 22, 2), (short)u.Patch);
        b[e + 24] = u.Specialty;
        int veh = 0; for (int i = 0; i < 16; i++) veh += (u.Roster >> (2 * i)) & 3;
        b[e + 25] = (byte)veh;
        b[e + 26] = u.Owner;
        Str(b, e + 27, baseName, 80);
        b[e + 107] = 0;
        BitConverter.TryWriteBytes(b.AsSpan(e + 108, 4), u.UnitFlags);
        BitConverter.TryWriteBytes(b.AsSpan(e + 112, 2), u.CampId);
        d.CmpChanged();
        return true;
    }

    public static bool Remove(CampaignDoc d, VuId id)
    {
        int at = Locate(d);
        if (at < 0) return false;
        int e = Find(d, at, id);
        if (e < 0) return false;
        var c = d.CmpRaw!;
        var nc = new byte[c.Length - Size];
        Array.Copy(c, 0, nc, 0, e);
        Array.Copy(c, e + Size, nc, e, c.Length - e - Size);
        BitConverter.TryWriteBytes(nc.AsSpan(at, 2), (ushort)(Count(d, at) - 1));
        d.CmpRaw = nc;
        d.CmpChanged();
        return true;
    }

    public static string ReadName(byte[] b, int o)
    {
        int z = Array.IndexOf(b, (byte)0, o, 80);
        return Encoding.Latin1.GetString(b, o, (z < 0 ? o + 80 : z) - o).Trim();
    }
}
