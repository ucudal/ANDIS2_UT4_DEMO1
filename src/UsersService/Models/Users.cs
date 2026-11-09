using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.Serialization;
using Ucu.Andis.Users.Soap;

namespace UsersService.Models;

// DataContract fija el nombre y el namespace del User en el XML y en el WSDL,
// para que no dependan del namespace de C#.
[Table("users")]
[DataContract(Namespace = UserServiceContract.Namespace)]
public class User
{
    [Column("id")]
    [DataMember]
    public int Id { get; set; }

    [Column("name")]
    [DataMember]
    public string Name { get; set; } = string.Empty;

    [Column("email")]
    [DataMember]
    public string Email { get; set; } = string.Empty;

    // A/B Testing: el nombre que se muestra, según la variante del experimento
    // -ver Experiment.cs-. Solo lo completa la versión 2, y no está en la base.
    // Sin valor, no se envía: para los clientes de la versión 1, el XML es el
    // mismo de antes.
    [NotMapped]
    [DataMember(EmitDefaultValue = false)]
    public string? DisplayName { get; set; }
}
