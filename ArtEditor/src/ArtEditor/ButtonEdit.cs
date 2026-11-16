namespace ArtEditor;

/// <summary>Come deve cambiare un pulsante: immagini degli stati, testo, suoni; a questo pulsante o a tutti quelli con la stessa immagine.</summary>
public sealed class ButtonStyle
{
    public string Normal = "";
    public string Archive = "";
    /// <summary>Immagine "premuto"/"disattivato": nome da scrivere nel .scf e, se nuova, l'immagine da aggiungere all'archivio.</summary>
    public string? PressedName, DisabledName;
    public Pic? PressedNew, DisabledNew;
    public (int X, int Y)? DownOffset;
    public (byte R, byte G, byte B)? Up, Down;
    /// <summary>null = non cambiare; "" = nessun suono.</summary>
    public string? PressSound, OverSound;
    public bool AllSame;
}

/// <summary>Lettura e scrittura nei file .scf dell'aspetto e del suono di un pulsante (senza finestre: si puo' provare da solo).</summary>
public static class ButtonEdit
{
    /// <summary>Immagini degli stati scritte nel .scf: normale, premuto, terzo stato (di solito NULL), disattivato.</summary>
    public static (string? N, string? P, string? Third, string? D, bool ImagesLine) States(ScfItem it)
    {
        if (it.Get("IMAGES") is { Length: > 0 } a)
            return (Nul(a[0]), a.Length > 1 ? Nul(a[1]) : null, a.Length > 2 ? a[2] : "NULL", a.Length > 3 ? Nul(a[3]) : null, true);
        string? S(params string[] st) => it.All("BUTTONIMAGE").Where(x => x.Length > 1 && st.Contains(x[0], StringComparer.OrdinalIgnoreCase)).Select(x => Nul(x[1])).FirstOrDefault();
        return (S("C_STATE_0", "C_STATE_UP"), S("C_STATE_1", "C_STATE_DOWN"), null, S("C_STATE_DISABLED"), false);
        static string? Nul(string s) => s.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    /// <summary>Scrive nel .scf le scelte per un pulsante: immagini degli stati, testo premuto, colori, suoni.</summary>
    /// <summary>Momenti del clic usati dai pulsanti di BMS in [SOUNDBITE].</summary>
    public static readonly string[] PressEvents = { "C_TYPE_LMOUSEDOWN", "C_TYPE_LMOUSEUP", "C_STATE_DOWN" };

    /// <summary>Pulsanti radio/interruttore: lo stato 1 e' "scelto" (resta cosi' finche' se ne sceglie un altro), non "premuto".</summary>
    public static bool Toggle(ScfItem it) => it.Get("SETUP") is { } s && s.Any(x => x.Equals("C_TYPE_TOGGLE", StringComparison.OrdinalIgnoreCase));

    public static bool Sticky(ScfItem it) => it.Get("SETUP") is { } s && s.Any(x => x.Equals("C_TYPE_RADIO", StringComparison.OrdinalIgnoreCase) || x.Equals("C_TYPE_TOGGLE", StringComparison.OrdinalIgnoreCase));

    public static List<string> Apply(List<string> lines, ScfItem it, ButtonStyle r)
    {
        var st = States(it);
        if (r.PressedName != null || r.DisabledName != null)
        {
            if (st.ImagesLine)
            {
                string n = st.N ?? r.Normal, p = r.PressedName ?? st.P ?? "NULL", d = r.DisabledName ?? st.D ?? n;
                lines = Scf.SetLines(lines, it, Scf.Key("[IMAGES]"), new[] { $"[IMAGES] {n} {p} {st.Third ?? "NULL"} {d}" });
            }
            else
            {
                if (r.PressedName != null) lines = Scf.SetLines(lines, it, t => Scf.Key("[BUTTONIMAGE]", "C_STATE_1")(t) || Scf.Key("[BUTTONIMAGE]", "C_STATE_DOWN")(t), new[] { "[BUTTONIMAGE] C_STATE_1 " + r.PressedName });
                if (r.DisabledName != null) lines = Scf.SetLines(lines, it, Scf.Key("[BUTTONIMAGE]", "C_STATE_DISABLED"), new[] { "[BUTTONIMAGE] C_STATE_DISABLED " + r.DisabledName });
            }
        }
        if (r.DownOffset is { } o)
            lines = Scf.SetLines(lines, it, Scf.Key("[TEXTOFFSET]", "C_STATE_DOWN"), o == (0, 0) ? Array.Empty<string>() : new[] { $"[TEXTOFFSET] C_STATE_DOWN {o.X} {o.Y}" });
        bool textColor = it.All("TEXTCOLOR").Any();
        if (r.Up is { } u)
            lines = textColor ? Scf.SetLines(lines, it, t => Scf.Key("[TEXTCOLOR]", "C_STATE_0")(t) || Scf.Key("[TEXTCOLOR]", "C_STATE_UP")(t), new[] { $"[TEXTCOLOR] C_STATE_0 {u.R} {u.G} {u.B}" })
                              : Scf.SetLines(lines, it, Scf.Key("[UPCOLOR]"), new[] { $"[UPCOLOR] {u.R} {u.G} {u.B}" });
        if (r.Down is { } dn)
            lines = textColor ? Scf.SetLines(lines, it, t => Scf.Key("[TEXTCOLOR]", "C_STATE_1")(t) || Scf.Key("[TEXTCOLOR]", "C_STATE_DOWN")(t), new[] { $"[TEXTCOLOR] C_STATE_1 {dn.R} {dn.G} {dn.B}" })
                              : Scf.SetLines(lines, it, Scf.Key("[DOWNCOLOR]"), new[] { $"[DOWNCOLOR] {dn.R} {dn.G} {dn.B}" });
        // suoni: [SOUNDBITE] SND_X C_TYPE_LMOUSEDOWN (clic) e C_TYPE_MOUSEOVER (passaggio del mouse)
        bool Ev(string t, params string[] ev) { if (!t.StartsWith("[SOUNDBITE]", StringComparison.OrdinalIgnoreCase)) return false; var a = Scf.Split(t[11..]); return a.Length > 1 && ev.Contains(a[1], StringComparer.OrdinalIgnoreCase); }
        if (r.PressSound != null)
        {
            // il momento del clic resta quello che il pulsante usava (in BMS: C_TYPE_LMOUSEDOWN, C_TYPE_LMOUSEUP o C_STATE_DOWN)
            string ev = it.All("SOUNDBITE").FirstOrDefault(a => a.Length > 1 && PressEvents.Contains(a[1], StringComparer.OrdinalIgnoreCase))?[1] ?? "C_TYPE_LMOUSEDOWN";
            lines = Scf.SetLines(lines, it, t => Ev(t, PressEvents), r.PressSound == "" ? Array.Empty<string>() : new[] { $"[SOUNDBITE] {r.PressSound} {ev}" });
        }
        if (r.OverSound != null)
            lines = Scf.SetLines(lines, it, t => Ev(t, "C_TYPE_MOUSEOVER"), r.OverSound == "" ? Array.Empty<string>() : new[] { $"[SOUNDBITE] {r.OverSound} C_TYPE_MOUSEOVER" });
        return lines;
    }

}
