"use client";

import Link from "next/link";
import { useCallback, useId, useRef, useState } from "react";

import type { ProblemDetails, UploadAccepted } from "@/lib/contracts";
import { bffRoutes } from "@/lib/routes";
import { formatBytes } from "@/lib/format";

const ACCEPTED = ".pdf,.png,.jpg,.jpeg,.tif,.tiff,application/pdf,image/png,image/jpeg,image/tiff";

interface UploadOutcome {
  fileName: string;
  accepted?: UploadAccepted;
  replayed?: boolean;
  error?: string;
}

interface UploadFormProps {
  maxSizeBytes: number;
  maxPageCount: number;
}

export function UploadForm({ maxSizeBytes, maxPageCount }: UploadFormProps) {
  const [files, setFiles] = useState<File[]>([]);
  const [expectedDocumentType, setExpectedDocumentType] = useState("");
  const [externalReference, setExternalReference] = useState("");
  const [isDragging, setIsDragging] = useState(false);
  const [isSending, setIsSending] = useState(false);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [outcomes, setOutcomes] = useState<UploadOutcome[]>([]);

  const inputRef = useRef<HTMLInputElement>(null);
  const typeFieldId = useId();
  const referenceFieldId = useId();

  const addFiles = useCallback((incoming: FileList | null) => {
    if (!incoming || incoming.length === 0) {
      return;
    }

    setFiles((current) => {
      const merged = [...current];
      for (const file of Array.from(incoming)) {
        const alreadyPicked = merged.some(
          (candidate) => candidate.name === file.name && candidate.size === file.size,
        );
        if (!alreadyPicked) {
          merged.push(file);
        }
      }
      return merged;
    });
  }, []);

  async function sendOne(file: File): Promise<UploadOutcome> {
    const body = new FormData();
    body.set("file", file, file.name);
    if (expectedDocumentType.trim()) {
      body.set("expectedDocumentType", expectedDocumentType.trim());
    }
    if (externalReference.trim()) {
      body.set("externalReference", externalReference.trim());
    }

    try {
      const response = await fetch(bffRoutes.upload, { method: "POST", body });

      if (response.ok) {
        return {
          fileName: file.name,
          accepted: (await response.json()) as UploadAccepted,
          replayed: response.headers.get("Idempotency-Replayed") === "true",
        };
      }

      const problem = (await response.json()) as ProblemDetails;
      return {
        fileName: file.name,
        error: problem.detail ?? problem.title ?? `Falha com status ${response.status}.`,
      };
    } catch {
      return { fileName: file.name, error: "Não foi possível falar com o serviço de upload." };
    }
  }

  async function handleSubmit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (files.length === 0 || isSending) {
      return;
    }

    setIsSending(true);
    setOutcomes([]);
    setProgress({ done: 0, total: files.length });

    const results: UploadOutcome[] = [];
    for (const [index, file] of files.entries()) {
      const outcome = await sendOne(file);
      results.push(outcome);
      setOutcomes([...results]);
      setProgress({ done: index + 1, total: files.length });
    }

    setFiles([]);
    if (inputRef.current) {
      inputRef.current.value = "";
    }
    setIsSending(false);
  }

  return (
    <form className="card" onSubmit={handleSubmit}>
      <h2 className="card__title">Enviar documentos</h2>
      <p className="card__hint">
        PDF, PNG, JPEG e TIFF. Até {formatBytes(maxSizeBytes)} e {maxPageCount} páginas por arquivo.
        O formato é validado pela assinatura real do arquivo, não pela extensão.
      </p>

      <div
        className={isDragging ? "dropzone dropzone--active" : "dropzone"}
        onDragOver={(event) => {
          event.preventDefault();
          setIsDragging(true);
        }}
        onDragLeave={() => setIsDragging(false)}
        onDrop={(event) => {
          event.preventDefault();
          setIsDragging(false);
          addFiles(event.dataTransfer.files);
        }}
      >
        <p style={{ margin: 0 }}>Arraste arquivos aqui ou selecione abaixo.</p>
        <input
          ref={inputRef}
          type="file"
          multiple
          accept={ACCEPTED}
          onChange={(event) => addFiles(event.target.files)}
          style={{ marginTop: 12 }}
        />
        {files.length > 0 && (
          <ul className="dropzone__files">
            {files.map((file) => (
              <li key={`${file.name}-${file.size}`}>
                {file.name} · {formatBytes(file.size)}
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="field-grid">
        <div>
          <label htmlFor={typeFieldId}>Tipo esperado (opcional)</label>
          <input
            id={typeFieldId}
            type="text"
            value={expectedDocumentType}
            placeholder="BR_CNH, BR_CNPJ_CARD, ..."
            maxLength={64}
            onChange={(event) => setExpectedDocumentType(event.target.value)}
          />
        </div>
        <div>
          <label htmlFor={referenceFieldId}>Referência externa (opcional)</label>
          <input
            id={referenceFieldId}
            type="text"
            value={externalReference}
            placeholder="CLIENTE-123"
            maxLength={128}
            onChange={(event) => setExternalReference(event.target.value)}
          />
        </div>
      </div>

      <div className="actions">
        <button type="submit" className="primary" disabled={isSending || files.length === 0}>
          {isSending ? "Enviando..." : `Enviar ${files.length || ""}`.trim()}
        </button>
        {files.length > 0 && !isSending && (
          <button
            type="button"
            onClick={() => {
              setFiles([]);
              if (inputRef.current) {
                inputRef.current.value = "";
              }
            }}
          >
            Limpar seleção
          </button>
        )}
        {progress && (
          <span style={{ fontSize: 14 }}>
            {progress.done} de {progress.total} processado(s)
          </span>
        )}
      </div>

      {outcomes.map((outcome) =>
        outcome.accepted ? (
          <div className="alert alert--success" key={`${outcome.fileName}-${outcome.accepted.id}`}>
            <strong>{outcome.fileName}</strong> aceito. Protocolo{" "}
            <span className="mono">{outcome.accepted.protocol}</span>
            {outcome.replayed && " (resposta repetida por Idempotency-Key)"} ·{" "}
            <Link href={`/documents/${outcome.accepted.id}`}>abrir documento</Link>
          </div>
        ) : (
          <div className="alert alert--error" key={`${outcome.fileName}-error`}>
            <strong>{outcome.fileName}</strong> recusado. {outcome.error}
          </div>
        ),
      )}
    </form>
  );
}
