import { redirect } from "next/navigation";

import { signIn } from "@/auth";
import { isOidcConfigured } from "@/lib/auth-config";

// The container image is built once and reused for both docker-compose.yml (anonymous) and
// docker-compose.dev.yml (OIDC): OIDC_AUTHORITY is a runtime env var, never present at `next build`
// time. Without this, Next.js prerenders this page as static at build time (with isOidcConfigured
// always false then) and every container would serve that frozen build-time decision instead of
// reading its own runtime environment - confirmed by a local build+run: without `force-dynamic` this
// page came back "○ /login" (static) and kept redirecting to "/" even after the process was restarted
// with OIDC_AUTHORITY set.
export const dynamic = "force-dynamic";

/** Minimal on purpose, consistent with the rest of this app's styling (globals.css's .card). */
export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ callbackUrl?: string }>;
}) {
  if (!isOidcConfigured) {
    redirect("/");
  }

  const { callbackUrl } = await searchParams;

  return (
    <div className="card" style={{ maxWidth: 420, margin: "64px auto" }}>
      <h1 className="card__title">Entrar</h1>
      <p className="card__hint">Autenticação via Keycloak (OpenID Connect).</p>
      <form
        action={async () => {
          "use server";
          await signIn("keycloak", { redirectTo: callbackUrl && callbackUrl.startsWith("/") ? callbackUrl : "/" });
        }}
      >
        <button type="submit" className="primary">
          Entrar com Keycloak
        </button>
      </form>
    </div>
  );
}
