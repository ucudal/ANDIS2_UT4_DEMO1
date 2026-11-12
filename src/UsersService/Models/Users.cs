using System.Runtime.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Ucu.Andis.Users.Soap;

namespace UsersService.Models;

// DataContract fija el nombre y el namespace del User en el XML y en el WSDL,
// para que no dependan del namespace de C#. Los atributos Bson fijan cómo se
// guarda en MongoDB: el id es un ObjectId, que en C# y en el XML viaja como
// texto de 24 caracteres hexadecimales.
[DataContract(Namespace = UserServiceContract.Namespace)]
public class User
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    [DataMember]
    public string? Id { get; set; }

    [BsonElement("name")]
    [DataMember]
    public string Name { get; set; } = string.Empty;

    [BsonElement("email")]
    [DataMember]
    public string Email { get; set; } = string.Empty;

    // A/B Testing: el nombre que se muestra, según la variante del experimento
    // -ver Experiment.cs-. Solo lo completa la versión 2, y no está en la base.
    // Sin valor, no se envía: para los clientes de la versión 1, el XML es el
    // mismo de antes.
    [BsonIgnore]
    [DataMember(EmitDefaultValue = false)]
    public string? DisplayName { get; set; }
}
