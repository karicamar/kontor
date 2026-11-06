using System.Xml;
using System.Xml.Linq;
using Kontor.Domain.Banking;

namespace Kontor.Domain.Sanctions;

public sealed record SanctionedParty(string Id, string Programme, string Type, IReadOnlyList<string> Names);

public sealed record SanctionsHit(SanctionedParty Party, string MatchedName, double Score);

/// <summary>
/// Screening against a sanctions list. Reads the XML format of the EU Consolidated Financial Sanctions List
/// (<c>export/sanctionEntity/nameAlias@wholeName</c>, published by the European Commission and the list German banks
/// must screen against). Every alias of an entry is screened; a name counts as a hit from <see cref="HitThreshold"/> and
/// as a possible match (manual review) from <see cref="ReviewThreshold"/>.
/// </summary>
public sealed class SanctionsList(IReadOnlyList<SanctionedParty> parties)
{
    public const double HitThreshold = 0.92;
    public const double ReviewThreshold = 0.85;

    public static SanctionsList Empty { get; } = new([]);

    public IReadOnlyList<SanctionedParty> Parties { get; } = parties;

    public static SanctionsList LoadEuXml(Stream xml)
    {
        // The real file is large but trusted; still, never resolve DTDs or external entities (XXE)
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var parties = doc.Descendants().Where(e => e.Name.LocalName == "sanctionEntity").Select(e =>
        {
            string? Attr(XElement x, string name) => (string?)x.Attribute(name);
            var names = e.Elements().Where(x => x.Name.LocalName == "nameAlias")
                .Select(a => Attr(a, "wholeName") is { Length: > 0 } whole
                    ? whole
                    : string.Join(' ', new[] { Attr(a, "firstName"), Attr(a, "middleName"), Attr(a, "lastName") }.Where(s => !string.IsNullOrWhiteSpace(s))))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var programme = e.Elements().FirstOrDefault(x => x.Name.LocalName == "regulation") is { } reg ? Attr(reg, "programme") ?? "" : "";
            var type = e.Elements().FirstOrDefault(x => x.Name.LocalName == "subjectType") is { } st ? Attr(st, "code") ?? "" : "";
            var id = Attr(e, "euReferenceNumber") ?? Attr(e, "logicalId") ?? "";
            return new SanctionedParty(id, programme, type, names);
        }).Where(p => p.Names.Count > 0).ToList();
        return new SanctionsList(parties);
    }

    /// <summary>The closest entry at or above <see cref="ReviewThreshold"/>, or null.</summary>
    public SanctionsHit? Screen(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        SanctionsHit? best = null;
        foreach (var party in Parties)
            foreach (var alias in party.Names)
            {
                var score = NameMatcher.Similarity(name, alias);
                if (score >= ReviewThreshold && (best is null || score > best.Score))
                    best = new SanctionsHit(party, alias, score);
            }
        return best;
    }
}
