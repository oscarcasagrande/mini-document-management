import Link from "next/link";
import { notFound } from "next/navigation";

import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { DeleteDocumentButton } from "@/components/DeleteDocumentButton";
import { DocumentStatusWatcher } from "@/components/DocumentStatusWatcher";
import { DocumentViewer } from "@/components/DocumentViewer";
import { StatusChip } from "@/components/StatusChip";
import { ApiError } from "@/lib/api";
import { IN_FLIGHT_STATUSES } from "@/lib/contracts";
import { getDocument } from "@/lib/documents";
import { bffRoutes } from "@/lib/routes";
import { formatBytes, formatConfidence, formatInstant, statusLabel } from "@/lib/format";

export const dynamic = "force-dynamic";

export default async function DocumentDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;

  let document;
  try {
    document = await getDocument(id);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      notFound();
    }
    throw error;
  }

  const { upload } = document;

  return (
    <>
      <h1>
        <span className="mono">{document.protocol}</span>
      </h1>
      <p className="subtitle">
        {upload.fileName} · <StatusChip status={document.status} />
      </p>

      <AnonymousAccessBanner />

      {document.lastError && (
        <div className="alert alert--error">
          <strong>Falha no processamento ({document.lastError.code}).</strong>{" "}
          {document.lastError.message} O arquivo original continua disponível abaixo.
        </div>
      )}

      <div className="detail-layout">
        <section className="card">
          <h2 className="card__title">Visualizador</h2>
          <p className="card__hint">
            {upload.mimeType} · {formatBytes(upload.sizeBytes)} · {upload.pageCount} página(s)
          </p>
          <DocumentViewer
            documentId={document.id}
            fileName={upload.fileName}
            mimeType={upload.mimeType}
          />
          <div className="actions" style={{ marginTop: 14 }}>
            <a className="mono" href={bffRoutes.download(document.id)}>
              Baixar original
            </a>
            <a className="mono" href={bffRoutes.content(document.id)} target="_blank" rel="noreferrer">
              Abrir em nova aba
            </a>
            <DeleteDocumentButton
              documentId={document.id}
              protocol={document.protocol}
              redirectTo="/documents"
            />
          </div>
        </section>

        <div>
          <section className="card">
            <h2 className="card__title">Metadados</h2>
            <dl className="meta">
              <dt>Id</dt>
              <dd className="mono">{document.id}</dd>
              <dt>Protocolo</dt>
              <dd className="mono">{document.protocol}</dd>
              <dt>Status</dt>
              <dd>
                {statusLabel(document.status)} <span className="mono">({document.status})</span>
              </dd>
              <dt>Canal</dt>
              <dd>{upload.channel}</dd>
              <dt>Enviado em</dt>
              <dd>{formatInstant(upload.uploadedAt)}</dd>
              <dt>Concluído em</dt>
              <dd>{formatInstant(upload.completedAt)}</dd>
              <dt>Tipo esperado</dt>
              <dd>{upload.expectedDocumentType ?? "—"}</dd>
              <dt>Referência externa</dt>
              <dd>{upload.externalReference ?? "—"}</dd>
              <dt>MIME detectado</dt>
              <dd className="mono">{upload.mimeType}</dd>
              <dt>Tamanho</dt>
              <dd>{formatBytes(upload.sizeBytes)}</dd>
              <dt>Páginas</dt>
              <dd>{upload.pageCount}</dd>
              <dt>SHA-256</dt>
              <dd className="mono">{upload.sha256}</dd>
            </dl>
          </section>

          <section className="card">
            <h2 className="card__title">Leitura</h2>
            {document.classification ? (
              <dl className="meta">
                <dt>Tipo identificado</dt>
                <dd>{document.classification.detectedType}</dd>
                <dt>Confiança</dt>
                <dd>{formatConfidence(document.classification.confidence)}</dd>
              </dl>
            ) : (
              <p className="card__hint" style={{ marginBottom: 0 }}>
                Tipo ainda não identificado: <span className="mono">UNKNOWN</span>.
              </p>
            )}
            <p className="stage-note">
              Texto bruto, campos estruturados e confiança por campo entram nas Etapas 2 e 3 do plano
              de execução. Nesta etapa o worker e o serviço de OCR sobem como stubs, então o documento
              permanece na fila.
            </p>
          </section>

          <section className="card">
            <h2 className="card__title">Processamento</h2>
            <ol className="timeline">
              {document.timeline.map((entry) => (
                <li key={`${entry.eventType}-${entry.occurredAt}`}>
                  <strong className="mono">{entry.eventType}</strong> · {formatInstant(entry.occurredAt)}
                  {entry.details && <div style={{ color: "var(--text-muted)" }}>{entry.details}</div>}
                </li>
              ))}
            </ol>
          </section>

          <p>
            <Link href="/documents">Voltar para a lista</Link>
          </p>
        </div>
      </div>

      {IN_FLIGHT_STATUSES.has(document.status) && (
        <DocumentStatusWatcher documentId={document.id} currentStatus={document.status} />
      )}
    </>
  );
}
