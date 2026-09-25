import type { DocumentProcessing, DocumentStatus, FieldValidationStatus } from "./contracts";

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

const FIELD_LABELS: Record<string, string> = {
  cpf: "CPF",
  name: "Nome",
  birthDate: "Data de nascimento",
};

/** Field names come from the schema of the type; unknown ones are shown as they are. */
export function fieldLabel(path: string): string {
  return FIELD_LABELS[path] ?? path;
}

const VALIDATION_LABELS: Record<FieldValidationStatus, string> = {
  VALID: "Válido",
  INVALID: "Inválido",
  NOT_FOUND: "Não encontrado",
  UNCERTAIN: "Incerto",
};

export function validationLabel(status: FieldValidationStatus): string {
  return VALIDATION_LABELS[status] ?? status;
}

export function validationTone(status: FieldValidationStatus): "pending" | "done" | "error" {
  if (status === "VALID") {
    return "done";
  }

  return status === "INVALID" ? "error" : "pending";
}

/** Explains, in Portuguese, the machine readable codes the extractor attaches to a field. */
const VALIDATION_MESSAGES: Record<string, string> = {
  CHECK_DIGIT_VALID: "Dígitos verificadores conferem (módulo 11)",
  CHECK_DIGIT_INVALID: "Dígitos verificadores não conferem",
  DATE_VALID: "Data real de calendário, não futura",
  NO_LABEL_NEARBY: "Achado sem o rótulo ao lado; confiança reduzida",
};

export function validationMessage(code: string): string {
  return VALIDATION_MESSAGES[code] ?? code;
}

/** One line that says what the worker is doing with the document right now. */
export function processingSummary(status: DocumentStatus, processing: DocumentProcessing | null): string | null {
  if (!processing) {
    return null;
  }

  const attempt = `tentativa ${processing.attempt} de ${processing.maxAttempts}`;
  const pages =
    processing.pageCount === null ? "" : ` · página(s) lidas: ${processing.pagesCompleted} de ${processing.pageCount}`;

  if (processing.jobStatus === "PENDING" && processing.attempt > 0 && processing.nextAttemptAt) {
    return `Nova tentativa agendada para ${formatInstant(processing.nextAttemptAt)} (${attempt}).`;
  }

  if (processing.jobStatus === "RUNNING") {
    return `${statusLabel(status)} · ${attempt}${pages}`;
  }

  return null;
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
