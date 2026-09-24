import type { DocumentStatus } from "./contracts";

/** Formats a UTC instant for a Brazilian reader, keeping it explicit that the value is UTC. */
export function formatInstant(value: string | null | undefined): string {
  if (!value) {
    return "—";
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  const formatted = new Intl.DateTimeFormat("pt-BR", {
    dateStyle: "short",
    timeStyle: "medium",
    timeZone: "UTC",
  }).format(date);

  return `${formatted} UTC`;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let unitIndex = 0;

  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }

  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`;
}

export function formatConfidence(value: number | null | undefined): string {
  return value === null || value === undefined ? "—" : `${Math.round(value * 100)}%`;
}

/** Labels in Portuguese for the interface; the wire value stays the canonical one. */
const STATUS_LABELS: Record<DocumentStatus, string> = {
  RECEIVED: "Recebido",
  STORED: "Armazenado",
  QUEUED: "Na fila",
  PREPROCESSING: "Pré-processando",
  OCR_RUNNING: "Executando OCR",
  CLASSIFYING: "Classificando",
  EXTRACTING: "Extraindo",
  COMPLETED: "Concluído",
  FAILED: "Falhou",
  REJECTED: "Rejeitado",
};

export function statusLabel(status: DocumentStatus): string {
  return STATUS_LABELS[status] ?? status;
}

/** Groups statuses into the three visual tones used by the status chip. */
export function statusTone(status: DocumentStatus): "pending" | "done" | "error" {
  if (status === "COMPLETED") {
    return "done";
  }

  if (status === "FAILED" || status === "REJECTED") {
    return "error";
  }

  return "pending";
}
