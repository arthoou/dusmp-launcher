namespace DusmpLauncher.Localization;

/// <summary>
/// Language metadata. Only entries with an embedded translation are selectable
/// in the launcher; the wider catalog can also describe API content languages.
/// <para>
/// The data lives in a pipe-separated table instead of 180 constructor calls so
/// that a reviewer can check an endonym against a reference list by reading one
/// line, and so that adding a language is a one-line change.
/// </para>
/// </summary>
public static class LanguageCatalog
{
    /// <summary>
    /// Tag the launcher falls back to when nothing else matches. Kept here
    /// rather than in the manager because the catalog is what guarantees the
    /// entry exists at all.
    /// </summary>
    public const string DefaultTag = "en-US";

    /// <summary>
    /// Group keys in the order the selection screen shows them. The UI resolves
    /// each through <c>onboarding.language.group.&lt;key&gt;</c>; it never invents
    /// its own headings.
    /// </summary>
    public static readonly IReadOnlyList<string> GroupOrder = new[]
    {
        "popular",
        "europe",
        "americas",
        "africa",
        "middleEast",
        "southAsia",
        "eastAsia",
        "southeastAsia",
        "oceania",
        "constructed",
    };

    /// <summary>
    /// Discover the translations actually packaged in this executable. A catalog
    /// entry alone must never advertise a translation that does not exist.
    /// </summary>
    internal const string TranslationResourcePrefix = "DusmpLauncher.Localization.Languages.";
    public static readonly IReadOnlyList<string> TranslatedTags = typeof(LanguageCatalog).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(TranslationResourcePrefix, StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal))
        .Select(name => name[TranslationResourcePrefix.Length..^5])
        .ToArray();

    /// <summary>
    /// Scripts written right to left. Broader than the shipped translations
    /// because the flag is metadata about the language, not about our strings —
    /// the manager only flips the UI for a language it can actually render.
    /// </summary>
    private static readonly HashSet<string> RightToLeftTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar-SA", "ary", "he", "fa", "ur", "ps", "sd", "ks", "yi", "ckb", "ug", "dv",
    };

    /// <summary>
    /// <c>tag|English name|native name|ISO 3166-1 alpha-2 for the flag|group</c>.
    /// An empty flag column means no country flag represents the language
    /// honestly (Catalan, Esperanto, Quechua…), which the contract allows.
    /// Native names are endonyms in the language's own script: a wrong endonym is
    /// worse than none, so anything uncertain was left out of this table rather
    /// than guessed.
    /// </summary>
    private const string Table = """
        # ── popular: the languages the launcher ships strings for ──────────────
        en-US|English|English|US|popular
        pt-BR|Portuguese (Brazil)|Português (Brasil)|BR|popular
        es-ES|Spanish|Español|ES|popular
        fr-FR|French|Français|FR|popular
        de-DE|German|Deutsch|DE|popular
        ru-RU|Russian|Русский|RU|popular
        pl-PL|Polish|Polski|PL|popular
        ja-JP|Japanese|日本語|JP|popular
        zh-CN|Chinese (Simplified)|简体中文|CN|popular
        ar-SA|Arabic|العربية|SA|popular
        it-IT|Italian|Italiano|IT|popular
        id-ID|Indonesian|Bahasa Indonesia|ID|popular
        tr-TR|Turkish|Türkçe|TR|popular
        ko-KR|Korean|한국어|KR|popular
        uk-UA|Ukrainian|Українська|UA|popular
        cs-CZ|Czech|Čeština|CZ|popular
        nl-NL|Dutch|Nederlands|NL|popular

        # ── europe (includes the languages of the Russian Federation) ──────────
        ab|Abkhaz|Аҧсшәа||europe
        sq|Albanian|Shqip|AL|europe
        ba|Bashkir|Башҡортса||europe
        eu|Basque|Euskara||europe
        be|Belarusian|Беларуская|BY|europe
        bs|Bosnian|Bosanski|BA|europe
        br|Breton|Brezhoneg||europe
        bg|Bulgarian|Български|BG|europe
        ca|Catalan|Català||europe
        ce|Chechen|Нохчийн||europe
        cv|Chuvash|Чӑвашла||europe
        co|Corsican|Corsu||europe
        crh|Crimean Tatar|Qırımtatarca||europe
        hr|Croatian|Hrvatski|HR|europe
        da|Danish|Dansk|DK|europe
        et|Estonian|Eesti|EE|europe
        fo|Faroese|Føroyskt|FO|europe
        fi|Finnish|Suomi|FI|europe
        fy|Frisian|Frysk||europe
        fur|Friulian|Furlan||europe
        gl|Galician|Galego||europe
        el|Greek|Ελληνικά|GR|europe
        hu|Hungarian|Magyar|HU|europe
        is|Icelandic|Íslenska|IS|europe
        ga|Irish|Gaeilge|IE|europe
        lv|Latvian|Latviešu|LV|europe
        lij|Ligurian|Ligure||europe
        lt|Lithuanian|Lietuvių|LT|europe
        lmo|Lombard|Lombard||europe
        lb|Luxembourgish|Lëtzebuergesch|LU|europe
        mk|Macedonian|Македонски|MK|europe
        mt|Maltese|Malti|MT|europe
        nap|Neapolitan|Napulitano||europe
        nb|Norwegian|Norsk|NO|europe
        oc|Occitan|Occitan||europe
        os|Ossetian|Ирон||europe
        pms|Piedmontese|Piemontèis||europe
        pt-PT|Portuguese (Portugal)|Português (Portugal)|PT|europe
        ro|Romanian|Română|RO|europe
        rm|Romansh|Rumantsch||europe
        sc|Sardinian|Sardu||europe
        gd|Scottish Gaelic|Gàidhlig||europe
        sr|Serbian|Српски|RS|europe
        scn|Sicilian|Sicilianu||europe
        sk|Slovak|Slovenčina|SK|europe
        sl|Slovenian|Slovenščina|SI|europe
        sv|Swedish|Svenska|SE|europe
        tt|Tatar|Татарча||europe
        vec|Venetian|Vèneto||europe
        cy|Welsh|Cymraeg||europe
        sah|Yakut|Саха тыла||europe

        # ── americas ──────────────────────────────────────────────────────────
        ay|Aymara|Aymar aru||americas
        gn|Guarani|Avañe'ẽ|PY|americas
        ht|Haitian Creole|Kreyòl ayisyen|HT|americas
        jam|Jamaican Patois|Patwa|JM|americas
        yua|Maya (Yucatec)|Maaya T'aan||americas
        nhe|Nahuatl|Nāhuatl||americas
        pap|Papiamento|Papiamentu|AW|americas
        qu|Quechua|Runasimi||americas
        es-419|Spanish (Latin America)|Español (Latinoamérica)||americas

        # ── africa ────────────────────────────────────────────────────────────
        af|Afrikaans|Afrikaans|ZA|africa
        am|Amharic|አማርኛ|ET|africa
        ary|Arabic (Moroccan)|الدارجة|MA|africa
        bm|Bambara|Bamanankan|ML|africa
        ny|Chichewa|Chichewa|MW|africa
        ee|Ewe|Eʋegbe|GH|africa
        ff|Fulani|Fulfulde||africa
        ha|Hausa|Hausa|NG|africa
        ig|Igbo|Igbo|NG|africa
        rw|Kinyarwanda|Ikinyarwanda|RW|africa
        rn|Kirundi|Ikirundi|BI|africa
        kri|Krio|Krio|SL|africa
        ln|Lingala|Lingála|CD|africa
        lg|Luganda|Luganda|UG|africa
        luo|Luo|Dholuo||africa
        mg|Malagasy|Malagasy|MG|africa
        mfe|Mauritian Creole|Kreol Morisien|MU|africa
        om|Oromo|Oromoo|ET|africa
        nso|Sepedi|Sepedi|ZA|africa
        st|Sesotho|Sesotho|LS|africa
        tn|Setswana|Setswana|BW|africa
        crs|Seychellois Creole|Kreol Seselwa|SC|africa
        sn|Shona|chiShona|ZW|africa
        so|Somali|Soomaali|SO|africa
        sw|Swahili|Kiswahili|TZ|africa
        ss|Swati|siSwati|SZ|africa
        tzm|Tamazight|Tamaziɣt||africa
        ti|Tigrinya|ትግርኛ|ER|africa
        ts|Tsonga|Xitsonga|ZA|africa
        tw|Twi|Twi|GH|africa
        ve|Venda|Tshivenḓa|ZA|africa
        wo|Wolof|Wolof|SN|africa
        xh|Xhosa|isiXhosa|ZA|africa
        yo|Yoruba|Yorùbá|NG|africa
        zu|Zulu|isiZulu|ZA|africa

        # ── middleEast: also Caucasus and Central Asia ────────────────────────
        hy|Armenian|Հայերեն|AM|middleEast
        az|Azerbaijani|Azərbaycan dili|AZ|middleEast
        ka|Georgian|ქართული|GE|middleEast
        he|Hebrew|עברית|IL|middleEast
        kk|Kazakh|Қазақ тілі|KZ|middleEast
        ku|Kurdish (Kurmanji)|Kurmancî||middleEast
        ckb|Kurdish (Sorani)|کوردیی ناوەندی||middleEast
        ky|Kyrgyz|Кыргызча|KG|middleEast
        ps|Pashto|پښتو|AF|middleEast
        fa|Persian|فارسی|IR|middleEast
        tg|Tajik|Тоҷикӣ|TJ|middleEast
        tk|Turkmen|Türkmen dili|TM|middleEast
        ug|Uyghur|ئۇيغۇرچە||middleEast
        uz|Uzbek|Oʻzbek|UZ|middleEast
        yi|Yiddish|ייִדיש||middleEast

        # ── southAsia ─────────────────────────────────────────────────────────
        as|Assamese|অসমীয়া|IN|southAsia
        awa|Awadhi|अवधी||southAsia
        bn|Bengali|বাংলা|BD|southAsia
        bho|Bhojpuri|भोजपुरी||southAsia
        brx|Bodo|बड़ो||southAsia
        dv|Dhivehi|ދިވެހި|MV|southAsia
        doi|Dogri|डोगरी||southAsia
        dz|Dzongkha|རྫོང་ཁ|BT|southAsia
        gu|Gujarati|ગુજરાતી|IN|southAsia
        hi|Hindi|हिन्दी|IN|southAsia
        kn|Kannada|ಕನ್ನಡ|IN|southAsia
        ks|Kashmiri|کٲشُر||southAsia
        gom|Konkani|कोंकणी||southAsia
        mai|Maithili|मैथिली||southAsia
        ml|Malayalam|മലയാളം|IN|southAsia
        mni-Mtei|Meiteilon (Manipuri)|ꯃꯤꯇꯩꯂꯣꯟ||southAsia
        mr|Marathi|मराठी|IN|southAsia
        lus|Mizo|Mizo ṭawng||southAsia
        ne|Nepali|नेपाली|NP|southAsia
        or|Odia|ଓଡ଼ିଆ|IN|southAsia
        pa|Punjabi|ਪੰਜਾਬੀ|IN|southAsia
        sa|Sanskrit|संस्कृतम्||southAsia
        sat|Santali|ᱥᱟᱱᱛᱟᱲᱤ||southAsia
        sd|Sindhi|سنڌي|PK|southAsia
        si|Sinhala|සිංහල|LK|southAsia
        ta|Tamil|தமிழ்|IN|southAsia
        te|Telugu|తెలుగు|IN|southAsia
        bo|Tibetan|བོད་སྐད་||southAsia
        ur|Urdu|اردو|PK|southAsia

        # ── eastAsia ──────────────────────────────────────────────────────────
        yue|Cantonese|粵語|HK|eastAsia
        zh-TW|Chinese (Traditional)|繁體中文|TW|eastAsia
        mn|Mongolian|Монгол|MN|eastAsia

        # ── southeastAsia ─────────────────────────────────────────────────────
        ace|Acehnese|Bahasa Acèh||southeastAsia
        ban|Balinese|Basa Bali||southeastAsia
        my|Burmese|မြန်မာ|MM|southeastAsia
        ceb|Cebuano|Cebuano||southeastAsia
        fil|Filipino|Filipino|PH|southeastAsia
        hil|Hiligaynon|Hiligaynon||southeastAsia
        hmn|Hmong|Hmoob||southeastAsia
        ilo|Ilocano|Ilokano||southeastAsia
        jv|Javanese|Basa Jawa||southeastAsia
        km|Khmer|ខ្មែរ|KH|southeastAsia
        lo|Lao|ລາວ|LA|southeastAsia
        ms|Malay|Bahasa Melayu|MY|southeastAsia
        min|Minangkabau|Baso Minangkabau||southeastAsia
        su|Sundanese|Basa Sunda||southeastAsia
        tet|Tetum|Tetun|TL|southeastAsia
        th|Thai|ไทย|TH|southeastAsia
        vi|Vietnamese|Tiếng Việt|VN|southeastAsia

        # ── oceania ───────────────────────────────────────────────────────────
        fj|Fijian|Na Vosa Vakaviti|FJ|oceania
        haw|Hawaiian|ʻŌlelo Hawaiʻi||oceania
        mi|Maori|Māori|NZ|oceania
        sm|Samoan|Gagana Sāmoa|WS|oceania
        to|Tongan|Lea faka-Tonga|TO|oceania
        tpi|Tok Pisin|Tok Pisin|PG|oceania

        # ── constructed and classical: no country, so no flag ─────────────────
        eo|Esperanto|Esperanto||constructed
        la|Latin|Latina||constructed
        """;

    private static readonly IReadOnlyList<LanguageInfo> AllLanguages = Parse();
    private static readonly Dictionary<string, LanguageInfo> ByTag = BuildTagIndex(AllLanguages);

    /// <summary>
    /// Every language, ordered the way the selection screen renders it: groups in
    /// <see cref="GroupOrder"/>, translated languages first inside a group, then
    /// by English name so the list is scannable by a player who is looking for a
    /// language whose script they cannot type.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> All => AllLanguages;

    /// <summary>Exact tag lookup, case-insensitive. Null when the tag is unknown.</summary>
    public static LanguageInfo? Find(string? tag)
        => tag is not null && ByTag.TryGetValue(Normalize(tag), out var info) ? info : null;

    /// <summary>
    /// First entry whose language subtag matches, in display order — so
    /// <c>pt</c> resolves to <c>pt-BR</c> rather than <c>pt-PT</c>, because
    /// popular (and therefore translated) entries sort first.
    /// </summary>
    public static LanguageInfo? FindByLanguagePart(string? tag, bool translatedOnly = false)
    {
        var part = LanguagePart(tag);
        if (part.Length == 0)
            return null;

        foreach (var info in AllLanguages)
        {
            if (translatedOnly && !info.HasTranslation)
                continue;
            if (string.Equals(LanguagePart(info.Tag), part, StringComparison.OrdinalIgnoreCase))
                return info;
        }

        return null;
    }

    /// <summary>
    /// The language subtag of a BCP-47 tag, lowercased: <c>pt-BR</c> to <c>pt</c>.
    /// Underscores are accepted because POSIX locales (<c>pt_BR.UTF-8</c>) reach
    /// us through environment variables on Linux.
    /// </summary>
    public static string LanguagePart(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "";

        var normalized = Normalize(tag);
        var cut = normalized.IndexOf('-');
        return (cut < 0 ? normalized : normalized[..cut]).ToLowerInvariant();
    }

    /// <summary>
    /// Trims a tag and folds the separators locales arrive with, without changing
    /// case: casing is only ever compared, never displayed, and the canonical
    /// spelling always comes from the catalog entry.
    /// </summary>
    internal static string Normalize(string tag)
    {
        var trimmed = tag.Trim().Replace('_', '-');

        // "pt_BR.UTF-8" and "pt_BR@euro" are both valid POSIX locale spellings.
        var dot = trimmed.IndexOfAny(new[] { '.', '@' });
        if (dot >= 0)
            trimmed = trimmed[..dot];

        return trimmed;
    }

    private static IReadOnlyList<LanguageInfo> Parse()
    {
        var rows = Table.Split('\n');
        var parsed = new List<(LanguageInfo Info, int GroupIndex, int RowIndex)>(rows.Length);
        var translated = new HashSet<string>(TranslatedTags, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i].Trim();
            if (row.Length == 0 || row[0] == '#')
                continue;

            var cells = row.Split('|');
            if (cells.Length != 5)
                throw new InvalidOperationException(
                    $"LanguageCatalog row {i + 1} has {cells.Length} columns, expected 5: {row}");

            var tag = cells[0].Trim();
            var group = cells[4].Trim();
            var groupIndex = Array.IndexOf(GroupOrder.ToArray(), group);
            if (groupIndex < 0)
                throw new InvalidOperationException(
                    $"LanguageCatalog row {i + 1} uses group '{group}', which is not in GroupOrder.");

            var info = new LanguageInfo(
                Tag: tag,
                EnglishName: cells[1].Trim(),
                NativeName: cells[2].Trim(),
                Flag: FlagFromCountryCode(cells[3].Trim()),
                Group: group,
                HasTranslation: translated.Contains(tag),
                IsRightToLeft: RightToLeftTags.Contains(tag));

            parsed.Add((info, groupIndex, i));
        }

        // "popular" keeps its hand-picked order (English, then the Brazilian
        // server's own language, then the rest by reach); every other group sorts
        // alphabetically so it stays navigable as rows are appended over time.
        return parsed
            .OrderBy(e => e.GroupIndex)
            .ThenBy(e => e.Info.HasTranslation ? 0 : 1)
            .ThenBy(e => e.GroupIndex == 0 ? "" : e.Info.EnglishName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.RowIndex)
            .Select(e => e.Info)
            .ToArray();
    }

    private static string? FlagFromCountryCode(string code)
    {
        if (code.Length != 2 || !code.All(c => c is >= 'A' and <= 'Z')) return null;
        return $"app://launcher/assets/flags/{code.ToLowerInvariant()}.svg";
    }

    private static Dictionary<string, LanguageInfo> BuildTagIndex(IReadOnlyList<LanguageInfo> languages)
    {
        var index = new Dictionary<string, LanguageInfo>(languages.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var language in languages)
        {
            if (!index.TryAdd(language.Tag, language))
                throw new InvalidOperationException($"LanguageCatalog lists tag '{language.Tag}' twice.");
        }

        foreach (var tag in TranslatedTags)
        {
            if (!index.ContainsKey(tag))
                throw new InvalidOperationException(
                    $"TranslatedTags lists '{tag}', which has no row in the catalog table.");
        }

        return index;
    }
}
