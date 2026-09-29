import "server-only";

import NextAuth from "next-auth";
import type { OIDCConfig } from "@auth/core/providers";

import {
  OIDC_AUTHORITY,
  OIDC_CLIENT_ID,
  OIDC_CLIENT_SECRET,
  OIDC_PUBLIC_AUTHORITY,
  isOidcConfigured,
} from "@/lib/auth-config";

interface KeycloakProfile {
  sub: string;
  name?: string;
  preferred_username?: string;
  email?: string;
  realm_access?: { roles?: unknown };
}

/**
 * A hand-built OIDC provider instead of `next-auth/providers/keycloak`, because that provider (and
 * Auth.js' OIDC support in general) discovers every endpoint from a single `issuer` via
 * `/.well-known/openid-configuration` — and the one Keycloak URL the browser can reach
 * (`NEXT_PUBLIC_OIDC_AUTHORITY`) is not the one this container can reach (`OIDC_AUTHORITY`); see
 * `lib/auth-config.ts`. Passing `authorization`/`token`/`userinfo` as plain URLs (not just an `issuer`)
 * makes `@auth/core` skip discovery entirely and use exactly the endpoints given below — confirmed by
 * reading its `handleOAuth`/`getAuthorizationUrl` source: discovery is only attempted when `token` and
 * `userinfo` are both absent.
 */
function keycloakProvider(): OIDCConfig<KeycloakProfile> {
  return {
    id: "keycloak",
    name: "Keycloak",
    type: "oidc",
    // Validated against the token's `iss` claim (oauth4webapi's validateIdTokenClaims). Keycloak fixes
    // `iss` to the public, browser-facing hostname regardless of which URL served the request, so this
    // must be the public authority, never OIDC_AUTHORITY.
    issuer: OIDC_PUBLIC_AUTHORITY,
    authorization: { url: `${OIDC_PUBLIC_AUTHORITY}/protocol/openid-connect/auth` },
    token: { url: `${OIDC_AUTHORITY}/protocol/openid-connect/token` },
    userinfo: { url: `${OIDC_AUTHORITY}/protocol/openid-connect/userinfo` },
    clientId: OIDC_CLIENT_ID,
    clientSecret: OIDC_CLIENT_SECRET,
    checks: ["pkce", "state"],
    // Roles come from `realm_access.roles` on the raw profile (id token claims), read directly in the
    // `jwt` callback below rather than here: this callback only shapes the `User` NextAuth would persist
    // through a database adapter, which this BFF does not use.
    profile(profile) {
      return {
        id: profile.sub,
        name: profile.name ?? profile.preferred_username,
        email: profile.email,
      };
    },
  };
}

export const { handlers, auth, signIn, signOut } = NextAuth({
  providers: isOidcConfigured ? [keycloakProvider()] : [],
  trustHost: true,
  // Never used for anything security sensitive when OIDC is off (no session is ever created), so a
  // fixed fallback is fine and keeps anonymous mode from throwing on a missing NEXTAUTH_SECRET.
  secret: process.env.NEXTAUTH_SECRET || "docreader-anonymous-mode-unused-secret",
  session: { strategy: "jwt" },
  pages: isOidcConfigured ? { signIn: "/login" } : undefined,
  callbacks: {
    jwt({ token, account, profile }) {
      if (account) {
        token.accessToken = account.access_token;
        token.idToken = account.id_token;
        token.accessTokenExpiresAt = account.expires_at ? account.expires_at * 1000 : undefined;
      }
      const realmAccess = (profile as KeycloakProfile | undefined)?.realm_access;
      if (Array.isArray(realmAccess?.roles)) {
        token.roles = realmAccess.roles.filter((role): role is string => typeof role === "string");
      }
      return token;
    },
    session({ session, token }) {
      session.accessToken = typeof token.accessToken === "string" ? token.accessToken : undefined;
      session.roles = Array.isArray(token.roles) ? token.roles : [];
      return session;
    },
  },
});
