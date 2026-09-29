import { NextResponse } from "next/server";

import { auth } from "@/auth";
import { isOidcConfigured } from "@/lib/auth-config";

const PUBLIC_PREFIXES = ["/login", "/api/auth", "/api/health"];

function isPublicPath(pathname: string): boolean {
  return PUBLIC_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`));
}

/**
 * Session gate for every route except the ones above, using Next.js 16's `proxy.ts` convention (the
 * successor to `middleware.ts`, which this Next version already flags as deprecated at build time).
 * Anonymous mode (OIDC_AUTHORITY unset, the default everywhere except docker-compose.dev.yml's keycloak
 * overlay) never calls `auth()` at all: this must behave exactly as it always has, with no login wall
 * and no session requirement.
 */
const protectedProxy = auth((request) => {
  if (isPublicPath(request.nextUrl.pathname)) {
    return NextResponse.next();
  }

  if (!request.auth) {
    const loginUrl = new URL("/login", request.nextUrl.origin);
    loginUrl.searchParams.set("callbackUrl", request.nextUrl.pathname + request.nextUrl.search);
    return NextResponse.redirect(loginUrl);
  }

  return NextResponse.next();
});

export default isOidcConfigured ? protectedProxy : () => NextResponse.next();

export const config = {
  matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"],
};
