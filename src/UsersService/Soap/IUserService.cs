using System.ServiceModel;
using Ucu.Andis.Users.Models;

namespace Ucu.Andis.Users.Soap;

/// <summary>
/// Define el contrato SOAP de Users.
/// </summary>
/// <remarks>
/// SoapCore lo publica en <c>/users/UserService.svc</c> y genera el WSDL a partir de
/// esta interfaz en <c>/users/UserService.svc?wsdl</c>.
/// </remarks>
[ServiceContract(Namespace = UserServiceContract.Namespace)]
public interface IUserService
{
    /// <summary>
    /// Devuelve un User, o <c>null</c> si el User no existe: la respuesta llega sin
    /// <c>GetUserResult</c>.
    /// </summary>
    /// <remarks>
    /// No es un SOAP Fault a propósito: en SOAP 1.1 todo Fault viaja como HTTP 500, y
    /// el Retry y el Circuit Breaker de Orders tratan el 500 como falla transitoria.
    /// Pedir un id inexistente terminaría reintentándose y abriendo el circuito. Un id
    /// que no es un ObjectId tampoco existe: también devuelve <c>null</c>.
    /// </remarks>
    [OperationContract]
    Task<User?> GetUser(string id);

    [OperationContract]
    Task<User[]> ListUsers();

    /// <summary>
    /// Crea un User y devuelve su id.
    /// </summary>
    /// <remarks>
    /// Si el email ya existe, responde un SOAP Fault con el motivo, que reemplaza al
    /// 409 de la API REST.
    /// </remarks>
    [OperationContract]
    Task<string> CreateUser(string name, string email);

    /// <summary>
    /// Cambia el nombre y el email de un User y publica el evento <c>Common.UserUpdated</c>
    /// -Publisher/Subscriber-.
    /// </summary>
    /// <remarks>
    /// Responde un SOAP Fault si el User no existe o si el email ya es de otro User.
    /// </remarks>
    [OperationContract]
    Task<User> UpdateUser(string id, string name, string email);
}

public static class UserServiceContract
{
    public const string Namespace = "http://ucu.edu.uy/andis/users";
}
