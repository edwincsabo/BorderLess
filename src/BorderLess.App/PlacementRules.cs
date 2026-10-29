namespace BorderLess.App;

internal static class PlacementRules
{
    public static readonly string[] Edges = { "none", "left", "right", "top", "bottom" };
    public static string Label(string edge) => edge switch { "left" => "Izquierdo", "right" => "Derecho", "top" => "Superior", "bottom" => "Inferior", _ => "Sin borde" };
    public static string Opposite(string edge) => edge switch { "left" => "right", "right" => "left", "top" => "bottom", "bottom" => "top", _ => "center" };
    public static Dictionary<Guid, string> Assign(IReadOnlyDictionary<Guid, string> current, Guid id, string edge)
    {
        if (!current.ContainsKey(id) || !Edges.Contains(edge)) throw new ArgumentException("Posición no válida.");
        var result = new Dictionary<Guid, string>(current);
        var previous = result[id];
        if (edge != "none") foreach (var occupant in result.Where(p => p.Key != id && p.Value == edge).Select(p => p.Key).ToArray()) result[occupant] = previous;
        result[id] = edge; return result;
    }
    public static void Validate(IReadOnlyDictionary<Guid, string> values)
    {
        if (values.Values.Any(e => !Edges.Contains(e)) || values.Values.Where(e => e != "none").GroupBy(e => e).Any(g => g.Count() > 1)) throw new ArgumentException("Solo puede haber un vecino por borde.");
    }
}
