import type { Metadata } from "next";
import Link from "next/link";
import type { ReactNode } from "react";

import { auth, signIn, signOut } from "@/auth";
import { ADMIN_ROLE, isOidcConfigured } from "@/lib/auth-config";

import "./globals.css";

export const metadata: Metadata = {
  title: "DocReader PoC",
  description: "Prova de conceito local de leitura de documentos, sem autenticação.",
};

const swaggerUrl = process.env.DOCREADER_PUBLIC_SWAGGER_URL ?? "http://localhost:8080/swagger";

export default async function RootLayout({ children }: { children: ReactNode }) {
  // Anonymous mode (the default everywhere except docker-compose.dev.yml's keycloak overlay) never
  // calls auth() at all, so this renders exactly as it always has.
  const session = isOidcConfigured ? await auth() : null;
  const isAdmin = (session?.roles ?? []).includes(ADMIN_ROLE);

  return (
    <html lang="pt-BR">
      <body>
        <header className="top-bar">
          <div className="top-bar__inner">
            <Link href="/" className="brand">
              DocReader<span>PoC local</span>
            </Link>
            <nav className="nav">
              <Link href="/">Enviar</Link>
              <Link href="/documents">Documentos</Link>
              <Link href="/config/product-services">Produtos</Link>
              <Link href="/config/retention-policies">Retenção</Link>
              <Link href="/config/storage-repositories">Repositórios</Link>
              <Link href="/config/webhook-subscriptions">Webhooks</Link>
              <Link href="/config/document-types">Tipos documentais</Link>
              <Link href="/config/audit-logs">Auditoria</Link>
              <a href={swaggerUrl} target="_blank" rel="noreferrer">
                Swagger
              </a>
            </nav>
            {isOidcConfigured ? (
              <nav className="nav">
                {session ? (
                  <>
                    <span className="muted small">
                      {session.user?.email ?? session.user?.name ?? "Sessão ativa"}
                      {isAdmin ? " · docreader-admin" : " · docreader-user"}
                    </span>
                    <form
                      action={async () => {
                        "use server";
                        await signOut({ redirectTo: "/" });
                      }}
                    >
                      <button type="submit" className="icon-button">
                        Sair
                      </button>
                    </form>
                  </>
                ) : (
                  <form
                    action={async () => {
                      "use server";
                      await signIn("keycloak");
                    }}
                  >
                    <button type="submit" className="icon-button">
                      Entrar
                    </button>
                  </form>
                )}
              </nav>
            ) : null}
          </div>
        </header>
        <main className="page">{children}</main>
      </body>
    </html>
  );
}
