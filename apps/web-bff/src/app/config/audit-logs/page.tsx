import Link from "next/link";
import { Suspense } from "react";

import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { AuditLogFilters } from "@/components/AuditLogFilters";
import { ApiError } from "@/lib/api";
import { listAuditLogs, type AuditLogFilters as Filters } from "@/lib/auditLogs";
import { formatInstant } from "@/lib/format";

export const dynamic = "force-dynamic";

type SearchParams = Record<string, string | string[] | undefined>;

function single(params: SearchParams, key: string): string | undefined {
  const value = params[key];
  const text = Array.isArray(value) ? value[0] : value;
  return text && text.trim().length > 0 ? text.trim() : undefined;
}

export default async function AuditLogsPage({ searchParams }: { searchParams: Promise<SearchParams> }) {
  const params = await searchParams;
  const page = Number(single(params, "page") ?? 1);
  const pageSize = Number(single(params, "pageSize") ?? 20);

  const filters: Filters = {
    userId: single(params, "userId"),
    action: single(params, "action"),
    from: single(params, "from"),
    to: single(params, "to"),
    page: Number.isFinite(page) && page > 0 ? page : 1,
    pageSize: Number.isFinite(pageSize) && pageSize > 0 ? pageSize : 20,
  };

  let result;
  let failure: string | null = null;

  try {
    result = await listAuditLogs(filters);
  } catch (error) {
    failure =
      error instanceof ApiError
        ? `${error.message} (correlationId ${error.correlationId})`
        : "Não foi possível falar com a API.";
  }

  const pageLink = (target: number) => {
    const next = new URLSearchParams();
    for (const [key, value] of Object.entries(filters)) {
      if (value !== undefined && value !== "" && key !== "page") {
        next.set(key, String(value));
      }
    }
    next.set("page", String(target));
    return `/config/audit-logs?${next.toString()}`;
  };

  return (
    <>
      <h1>Auditoria</h1>
      <p className="subtitle">
        Quem fez o quê, a que recurso e quando. Somente leitura: nada aqui é criado, alterado ou apagado por esta
        tela. <span className="mono">changes</span> é metadado do que uma alteração tocou (nomes de campo), nunca o
        valor em si — nenhum conteúdo de documento ou segredo aparece aqui.
      </p>

      <AnonymousAccessBanner />

      {failure && <div className="alert alert--error">{failure}</div>}

      <Suspense fallback={<div className="card">Carregando filtros...</div>}>
        <AuditLogFilters />
      </Suspense>

      {result && result.items.length === 0 && (
        <div className="card">
          <div className="empty">
            <p>Nenhuma ação registrada com esses filtros.</p>
          </div>
        </div>
      )}

      {result && result.items.length > 0 && (
        <div className="card">
          <table>
            <thead>
              <tr>
                <th>Quando</th>
                <th>Usuário</th>
                <th>Ação</th>
                <th>Recurso</th>
                <th>Alterações</th>
                <th>IP</th>
              </tr>
            </thead>
            <tbody>
              {result.items.map((entry) => (
                <tr key={entry.id}>
                  <td>{formatInstant(entry.occurredAt)}</td>
                  <td>
                    {entry.userId ? (
                      <span className="mono">{entry.userId}</span>
                    ) : (
                      <span className="muted" title="Sem autenticação nesta PoC">
                        —
                      </span>
                    )}
                  </td>
                  <td>
                    <span className="mono">{entry.action}</span>
                  </td>
                  <td>
                    {entry.resourceType} <span className="mono">{entry.resourceId}</span>
                  </td>
                  <td>
                    {entry.changes ? (
                      <span className="mono" style={{ fontSize: 12 }} title={entry.changes}>
                        {entry.changes}
                      </span>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                  <td>
                    <span className="mono" style={{ fontSize: 12 }} title={entry.userAgent ?? undefined}>
                      {entry.ipAddress ?? "—"}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="pagination">
            <span>
              {result.totalCount} registro(s) · página {result.page} de {Math.max(1, result.totalPages)}
            </span>
            <span className="actions">
              {result.page > 1 && <Link href={pageLink(result.page - 1)}>Anterior</Link>}
              {result.page < result.totalPages && <Link href={pageLink(result.page + 1)}>Próxima</Link>}
            </span>
          </div>
        </div>
      )}
    </>
  );
}
