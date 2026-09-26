import Link from "next/link";
import { Suspense } from "react";

import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { DeleteDocumentButton } from "@/components/DeleteDocumentButton";
import { DocumentFilters } from "@/components/DocumentFilters";
import { StatusChip } from "@/components/StatusChip";
import { ApiError } from "@/lib/api";
import { IN_FLIGHT_STATUSES } from "@/lib/contracts";
import { listDocuments, type DocumentFilters as Filters } from "@/lib/documents";
import { bffRoutes } from "@/lib/routes";
import { formatBytes, formatConfidence, formatInstant } from "@/lib/format";

export const dynamic = "force-dynamic";

type SearchParams = Record<string, string | string[] | undefined>;

function single(params: SearchParams, key: string): string | undefined {
  const value = params[key];
  const text = Array.isArray(value) ? value[0] : value;
  return text && text.trim().length > 0 ? text.trim() : undefined;
}

export default async function DocumentsPage({
  searchParams,
}: {
  searchParams: Promise<SearchParams>;
}) {
  const params = await searchParams;
  const page = Number(single(params, "page") ?? 1);
  const pageSize = Number(single(params, "pageSize") ?? 20);

  const filters: Filters = {
    protocol: single(params, "protocol"),
    fileName: single(params, "fileName"),
    documentType: single(params, "documentType"),
    productServiceCode: single(params, "productServiceCode"),
    externalReference: single(params, "externalReference"),
    channel: single(params, "channel"),
    status: single(params, "status"),
    uploadedFrom: single(params, "uploadedFrom"),
    uploadedTo: single(params, "uploadedTo"),
    page: Number.isFinite(page) && page > 0 ? page : 1,
    pageSize: Number.isFinite(pageSize) && pageSize > 0 ? pageSize : 20,
  };

  let result;
  let failure: string | null = null;

  try {
    result = await listDocuments(filters);
  } catch (error) {
    failure =
      error instanceof ApiError
        ? `${error.message} (correlationId ${error.correlationId})`
        : "Não foi possível falar com a API.";
  }

  const hasInFlight = (result?.items ?? []).some((item) => IN_FLIGHT_STATUSES.has(item.status));

  const pageLink = (target: number) => {
    const next = new URLSearchParams();
    for (const [key, value] of Object.entries(filters)) {
      if (value !== undefined && value !== "" && key !== "page") {
        next.set(key, String(value));
      }
    }
    next.set("page", String(target));
    return `/documents?${next.toString()}`;
  };

  return (
    <>
      <h1>Documentos</h1>
      <p className="subtitle">Todos os uploads, mais recentes primeiro. Consulta sem login.</p>

      <AnonymousAccessBanner />

      <Suspense fallback={<div className="card">Carregando filtros...</div>}>
        <DocumentFilters hasInFlightDocuments={hasInFlight} />
      </Suspense>

      {failure && <div className="alert alert--error">{failure}</div>}

      {result && result.items.length === 0 && (
        <div className="card">
          <div className="empty">
            <p>Nenhum documento encontrado.</p>
            <p>
              <Link href="/">Enviar o primeiro documento</Link>
            </p>
          </div>
        </div>
      )}

      {result && result.items.length > 0 && (
        <div className="card">
          <table>
            <thead>
              <tr>
                <th>Protocolo</th>
                <th>Arquivo</th>
                <th>Tipo</th>
                <th>Produto</th>
                <th>Canal</th>
                <th>Enviado em</th>
                <th>Status</th>
                <th>Confiança</th>
                <th>Ações</th>
              </tr>
            </thead>
            <tbody>
              {result.items.map((document) => (
                <tr key={document.id}>
                  <td className="mono">
                    <Link href={`/documents/${document.id}`}>{document.protocol}</Link>
                  </td>
                  <td>
                    {document.fileName}
                    <br />
                    {document.externalReference && (
                      <>
                        <span className="mono" style={{ fontSize: 12 }} title="Referência externa">
                          ref: {document.externalReference}
                        </span>
                        <br />
                      </>
                    )}
                    <span style={{ color: "var(--text-muted)", fontSize: 12 }}>
                      {document.mimeType} · {formatBytes(document.sizeBytes)} · {document.pageCount}{" "}
                      pág.
                    </span>
                  </td>
                  <td>{document.detectedDocumentType ?? "UNKNOWN"}</td>
                  <td>
                    {document.productService ? (
                      <span className="mono" title={document.productService.name}>
                        {document.productService.code}
                      </span>
                    ) : (
                      <span className="muted">—</span>
                    )}
                  </td>
                  <td>{document.channel}</td>
                  <td>{formatInstant(document.uploadedAt)}</td>
                  <td>
                    <StatusChip status={document.status} />
                  </td>
                  <td>{formatConfidence(document.classificationConfidence)}</td>
                  <td>
                    <div className="actions">
                      <Link href={`/documents/${document.id}`}>Abrir</Link>
                      <a href={bffRoutes.download(document.id)}>Baixar</a>
                      <DeleteDocumentButton documentId={document.id} protocol={document.protocol} />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          <div className="pagination">
            <span>
              {result.totalCount} documento(s) · página {result.page} de {Math.max(1, result.totalPages)}
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
