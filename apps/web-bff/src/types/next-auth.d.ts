import type { DefaultSession } from "next-auth";

/**
 * Adds the fields `auth.ts` puts on the session/token: the access token this BFF attaches as a Bearer
 * token when it calls the API (see `lib/api.ts`), and the realm roles from Keycloak's `realm_access`
 * claim (`docreader-admin` / `docreader-user`).
 */
declare module "next-auth" {
  interface Session extends DefaultSession {
    accessToken?: string;
    roles: string[];
  }
}

declare module "next-auth/jwt" {
  interface JWT {
    accessToken?: string;
    idToken?: string;
    accessTokenExpiresAt?: number;
    roles?: string[];
  }
}
