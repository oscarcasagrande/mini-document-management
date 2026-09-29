import { handlers } from "@/auth";

/**
 * Standard NextAuth.js route: /api/auth/signin, /api/auth/callback/keycloak, /api/auth/signout,
 * /api/auth/session, and so on. In anonymous mode (OIDC_AUTHORITY unset) `auth.ts` registers no
 * provider, so these answer with an empty provider list instead of ever redirecting anywhere.
 */
export const { GET, POST } = handlers;
