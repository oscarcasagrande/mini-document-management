import type { Metadata } from "next";
import Link from "next/link";
import type { ReactNode } from "react";

import "./globals.css";

export const metadata: Metadata = {
  title: "DocReader PoC",
  description: "Prova de conceito local de leitura de documentos, sem autenticação.",
};

const swaggerUrl = process.env.DOCREADER_PUBLIC_SWAGGER_URL ?? "http://localhost:8080/swagger";

export default function RootLayout({ children }: { children: ReactNode }) {
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
              <a href={swaggerUrl} target="_blank" rel="noreferrer">
                Swagger
              </a>
            </nav>
          </div>
        </header>
        <main className="page">{children}</main>
      </body>
    </html>
  );
}
