using System.ServiceModel;
using UsersService.Models;

namespace Ucu.Andis.Users.Soap;

// Contrato SOAP de Users. SoapCore lo publica en /users/UserService.svc y
// genera el WSDL a partir de esta interfaz en /users/UserService.svc?wsdl.
[ServiceContract(Namespace = UserServiceContract.Namespace)]
public interface IUserService
{
    // Devuelve null si el User no existe: la respuesta llega sin GetUserResult.
    // No es un SOAP Fault a propósito: en SOAP 1.1 todo Fault viaja como HTTP
    // 500, y el Retry y el Circuit Breaker de Orders tratan el 500 como falla
    // transitoria. Pedir un id inexistente terminaría reintentándose y abriendo
    // el circuito. Un id que no es un ObjectId tampoco existe: también devuelve
    // null.
    [OperationContract]
    Task<User?> GetUser(string id);

    [OperationContract]
    Task<User[]> ListUsers();

    // Devuelve el id del User creado. Si el email ya existe, responde un SOAP
    // Fault con el motivo, que reemplaza al 409 de la API REST.
    [OperationContract]
    Task<string> CreateUser(string name, string email);

    // Cambia el nombre y el email de un User y publica el evento UserUpdated
    // -Publisher/Subscriber-. Responde un SOAP Fault si el User no existe o si
    // el email ya es de otro User.
    [OperationContract]
    Task<User> UpdateUser(string id, string name, string email);
}

public static class UserServiceContract
{
    public const string Namespace = "http://ucu.edu.uy/andis/users";
}
