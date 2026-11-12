using System.Text.RegularExpressions;

namespace Ucu.Andis.OrdersService;

// Los ids de los Users son ObjectId de MongoDB: 24 caracteres hexadecimales.
// Orders los guarda como texto y no los interpreta, pero rechaza con 400 los
// que no tienen ese formato, en lugar de buscar Orders de un User que no puede
// existir.
public static partial class UserIds
{
    public const string InvalidDetail = "Invalid user id format";

    public static bool IsValid(string? id) => id is not null && ObjectIdFormat().IsMatch(id);

    [GeneratedRegex("^[0-9a-f]{24}$")]
    private static partial Regex ObjectIdFormat();
}
