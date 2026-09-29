import "server-only";

/**
 * OIDC is entirely optional. `docker-compose.yml` (the acceptance compose) never sets these, and an
 * empty `OIDC_AUTHORITY` must keep the BFF in exactly the anonymous mode it has always run in: no login
 * wall, no session requirement anywhere. Only `docker-compose.dev.yml`'s `keycloak` service (and its
 * matching `web-bff` overrides) turns this on, for testing the OIDC flow end to end. `auth.ts` and
 * `middleware.ts` both branch on {@link isOidcConfigured} before touching any auth machinery.
 *
 * Two different base URLs point at the *same* Keycloak realm, because the browser and this container
 * cannot reach Keycloak the same way:
 *
 * - `OIDC_AUTHORITY` — reachable only from inside the compose network (this container talking to the
 *   `keycloak` service by its Docker DNS name). Used to actually call the token and userinfo endpoints
 *   server side.
 * - `NEXT_PUBLIC_OIDC_AUTHORITY` — reachable from the host browser (`http://localhost:8081/...` by
 *   default). Used to build the redirect to Keycloak's login page. Keycloak's `KC_HOSTNAME` fixes its
 *   issuer to this exact value no matter which URL was actually used to reach it (confirmed against a
 *   running container: a discovery request made from inside the compose network still reports this
 *   address as `issuer`, while `token_endpoint`/`userinfo_endpoint` come back as the internal address
 *   because `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`) — so this is also the exact `iss` every token
 *   carries, and therefore what `auth.ts` validates the token issuer against, not `OIDC_AUTHORITY`.
 *
 * Falls back to `OIDC_AUTHORITY` when the public variable is not set, so a deployment that puts a single
 * reachable hostname in front of Keycloak (a real environment, unlike this dev-only container split)
 * only needs to set one variable.
 */
export const OIDC_AUTHORITY = (process.env.OIDC_AUTHORITY ?? "").trim().replace(/\/+$/, "");
export const OIDC_PUBLIC_AUTHORITY = ((process.env.NEXT_PUBLIC_OIDC_AUTHORITY ?? "").trim().replace(/\/+$/, "") ||
  OIDC_AUTHORITY);
export const OIDC_CLIENT_ID = (process.env.OIDC_CLIENT_ID ?? "").trim();
export const OIDC_CLIENT_SECRET = process.env.OIDC_CLIENT_SECRET ?? "";

export const isOidcConfigured = OIDC_AUTHORITY.length > 0 && OIDC_CLIENT_ID.length > 0;

/** Realm role that unlocks the admin-flavored parts of the UI, once there are any (see layout.tsx). */
export const ADMIN_ROLE = "docreader-admin";
