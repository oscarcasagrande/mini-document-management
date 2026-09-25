import Link from "next/link";
import { notFound } from "next/navigation";

import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { DeleteDocumentButton } from "@/components/DeleteDocumentButton";
import { DocumentStatusWatcher } from "@/components/DocumentStatusWatcher";
import { DocumentViewer } from "@/components/DocumentViewer";
import { ReprocessButton } from "@/components/ReprocessButton";
import { StatusChip } from "@/components/StatusChip";
import { ApiError } from "@/lib/api";
import { IN_FLIGHT_STATUSES } from "@/lib/contracts";
import { getDocument, getDocumentResult, getDocumentText } from "@/lib/documents";
import { bffRoutes } from "@/lib/routes";
import {
  fieldLabel,
  formatBytes,
  formatConfidence,
  formatInstant,
  processingSummary,
  statusLabel,
  validationLabel,
  validationMessage,
  validationTone,
} from "@/lib/format";

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
  const inFlight = IN_FLIGHT_STATUSES.has(document.status);

  // Only ask for the result when the API says there is one: a 409 here would be noise, not news.
  const [result, text] = document.extraction
    ? await Promise.all([getDocumentResult(id), getDocumentText(id)])
    : [null, null];

  const progress = processingSummary(document.status, document.processing);
  const retryScheduled = document.status === "QUEUED" && document.lastError !== null;

  return (
    <>
      <h1>
        <span className="mono">{document.protocol}</span>
      </h1>
      <p className="subtitle">
        {upload.fileName} · <StatusChip status={document.status} />
      </p>

      <AnonymousAccessBanner />

      {document.lastError && !retryScheduled && (
        <div className="alert alert--error">
          <strong>Falha no processamento ({document.lastError.code}).</strong>{" "}
          {document.lastError.message} O arquivo original continua disponível abaixo.
        </div>
      )}

      {retryScheduled && document.lastError && (
        <div className="alert alert--warning">
          <strong>A última tentativa falhou ({document.lastError.code}).</strong>{" "}
          {document.lastError.message} O documento voltou para a fila e será processado de novo
          automaticamente. O arquivo original continua disponível abaixo.
        </div>
      )}

      {progress && <p className="progress-line mono">{progress}</p>}

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
            <ReprocessButton documentId={document.id} disabled={inFlight} />
            <DeleteDocumentButton
              documentId={document.id}
              protocol={document.protocol}
              redirectTo="/documents"
            />
          </div>
        </section>

        <div>
          <section className="card">
            <h2 className="card__title">Leitura</h2>

            {document.classification ? (
              <dl className="meta">
                <dt>Tipo identificado</dt>
                <dd>
                  <span className="mono">{document.classification.detectedType}</span>
                </dd>
                <dt>Confiança da classificação</dt>
                <dd>{formatConfidence(document.classification.confidence)}</dd>
                {result && (
                  <>
                    <dt>Confiança geral dos campos</dt>
                    <dd>{formatConfidence(result.extraction.overallConfidence)}</dd>
                    <dt>Lido em</dt>
                    <dd>{formatInstant(result.extraction.extractedAt)}</dd>
                  </>
                )}
              </dl>
            ) : (
              <p className="card__hint" style={{ marginBottom: 0 }}>
                {inFlight
                  ? "Ainda sendo lido."
                  : "Tipo ainda não identificado: "}
                {!inFlight && <span className="mono">UNKNOWN</span>}
              </p>
            )}

            {result && inFlight && (
              <p className="stage-note">
                Mostrando o resultado da leitura anterior; uma nova leitura está em andamento e o
                resultado só troca quando ela terminar.
              </p>
            )}

            {result && Object.keys(result.extraction.fields).length > 0 && (
              <div className="table-scroll">
                <table className="fields">
                  <thead>
                    <tr>
                      <th>Campo</th>
                      <th>Lido</th>
                      <th>Normalizado</th>
                      <th>Confiança</th>
                      <th>Validação</th>
                    </tr>
                  </thead>
                  <tbody>
                    {Object.entries(result.extraction.fields).map(([path, field]) => (
                      <tr key={path}>
                        <td>
                          <strong>{fieldLabel(path)}</strong>
                          <div className="mono muted">{path}</div>
                        </td>
                        <td className="mono">{field.raw ?? "—"}</td>
                        <td className="mono">{field.normalized ?? "—"}</td>
                        <td>{formatConfidence(field.confidence)}</td>
                        <td>
                          <span className={`chip chip--${validationTone(field.validationStatus)}`}>
                            {validationLabel(field.validationStatus)}
                          </span>
                          {field.validationMessages.map((code) => (
                            <div key={code} className="muted small">
                              {validationMessage(code)}
                            </div>
                          ))}
                          {field.evidence.page !== null && (
                            <div className="muted small">página {field.evidence.page}</div>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {result && Object.keys(result.extraction.fields).length === 0 && (
              <p className="stage-note">
                Este tipo de documento ainda não tem extrator: o texto bruto foi lido e está abaixo, mas
                não há campos estruturados. Os demais tipos entram na Etapa 3.
              </p>
            )}

            {!result && !inFlight && document.status === "COMPLETED" && (
              <p className="stage-note">Não há resultado gravado para este documento.</p>
            )}
          </section>

          <section className="card">
            <h2 className="card__title">Texto bruto</h2>
            {text ? (
              <>
                <p className="card__hint">
                  Exatamente o que o OCR leu, na ordem de leitura. Nada foi corrigido ou interpretado.
                </p>
                {text.pages.map((page) => (
                  <details key={page.page} open={text.pages.length === 1}>
                    <summary>Página {page.page}</summary>
                    <pre className="raw-text">{page.text}</pre>
                  </details>
                ))}
                <p className="stage-note">
                  {text.ocrProvider} · <span className="mono">{text.ocrModelVersion}</span>
                </p>
              </>
            ) : (
              <p className="card__hint" style={{ marginBottom: 0 }}>
                {inFlight
                  ? "O texto aparece aqui quando a leitura terminar."
                  : "Nenhum texto foi lido deste documento."}
              </p>
            )}
          </section>

          <section className="card">
            <h2 className="card__title">Processamento</h2>
            <ol className="timeline">
              {document.timeline.map((entry) => (
                <li key={`${entry.eventType}-${entry.occurredAt}`}>
                  <strong className="mono">{entry.eventType}</strong> · {formatInstant(entry.occurredAt)}
                  {entry.details && <div className="muted small mono">{entry.details}</div>}
                </li>
              ))}
            </ol>
          </section>

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

          <p>
            <Link href="/documents">Voltar para a lista</Link>
          </p>
        </div>
      </div>

      {inFlight && (
        <DocumentStatusWatcher
          documentId={document.id}
          initial={{
            id: document.id,
            protocol: document.protocol,
            status: document.status,
            lastError: document.lastError,
            processing: document.processing,
          }}
        />
      )}
    </>
  );
}
