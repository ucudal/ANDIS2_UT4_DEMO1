// Federated Identity: lee los claims del token de Keycloak, para pasárselos a
// los servicios como encabezados -ver nginx.conf-.
//
// No verifica la firma: lo usa el servidor HTTPS después de que auth_request
// validó el token con Keycloak. Si el token no es válido, la solicitud ya se
// rechazó con 401 y estos valores no se usan.

// El payload es la parte del medio del token, en base64url.
function claims(r) {
    const match = (r.headersIn['Authorization'] || '').match(/^Bearer\s+[^.]+\.([^.]+)\./);
    if (!match)
        return {};
    try {
        return JSON.parse(Buffer.from(match[1], 'base64url').toString());
    } catch (e) {
        return {};
    }
}

// El id del User de la aplicación; vacío si el usuario no es cliente.
function userId(r) {
    const id = claims(r).user_id;
    return id === undefined ? '' : String(id);
}

// El nombre de usuario en Keycloak.
function userName(r) {
    return claims(r).preferred_username || '';
}

// Los roles del usuario, separados por coma.
function userRoles(r) {
    const access = claims(r).realm_access;
    return access && access.roles ? access.roles.join(',') : '';
}

export default { userId, userName, userRoles };
