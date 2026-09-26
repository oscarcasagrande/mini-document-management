import type { DocumentProcessing, DocumentStatus, FieldValidationStatus, RetentionScope } from "./contracts";

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
  PURGED: "Expurgado",
};

const SCOPE_LABELS: Record<RetentionScope, string> = {
  GLOBAL: "global",
  DOCUMENT_TYPE: "por tipo",
  PRODUCT_SERVICE: "por produto",
  DOCUMENT_TYPE_AND_PRODUCT_SERVICE: "por tipo e produto",
};

export function scopeLabel(scope: RetentionScope): string {
  return SCOPE_LABELS[scope] ?? scope;
}

export function statusLabel(status: DocumentStatus): string {
  return STATUS_LABELS[status] ?? status;
}

const FIELD_LABELS: Record<string, string> = {
  // Comuns a vários tipos
  cpf: "CPF",
  cnpj: "CNPJ",
  name: "Nome",
  birthDate: "Data de nascimento",
  issueDate: "Data de emissão",
  expirationDate: "Data de validade",
  openingDate: "Data de abertura",
  postalCode: "CEP",
  city: "Município",
  state: "UF",
  neighborhood: "Bairro",
  shareCapital: "Capital social",
  legalName: "Nome empresarial",
  tradeName: "Nome fantasia",
  mainActivityCode: "Atividade principal (código)",
  mainActivityDescription: "Atividade principal (descrição)",
  // CIN / RG
  rg: "Registro Geral (RG)",
  birthPlace: "Naturalidade",
  fatherName: "Nome do pai",
  motherName: "Nome da mãe",
  mrz: "Zona de leitura mecânica (MRZ)",
  // CNH
  registrationNumber: "Nº de registro",
  category: "Categoria",
  firstLicenseDate: "Data da 1ª habilitação",
  // Comprovante de residência
  holderName: "Titular",
  holderDocument: "CPF/CNPJ do titular",
  addressLine: "Endereço",
  referenceMonth: "Mês de referência",
  dueDate: "Vencimento",
  serviceType: "Tipo de serviço",
  // Cartão CNPJ
  legalNature: "Natureza jurídica",
  street: "Logradouro",
  number: "Número",
  complement: "Complemento",
  registrationStatus: "Situação cadastral",
  registrationStatusDate: "Data da situação cadastral",
  // CCMEI
  address: "Endereço comercial",
  holderCpf: "CPF do empresário",
  holderBirthDate: "Nascimento do empresário",
  certificateDate: "Data do certificado",
  // Contrato social
  companyName: "Denominação social",
  nire: "NIRE",
  headquarters: "Sede",
  headquartersPostalCode: "CEP da sede",
  corporatePurpose: "Objeto social",
  contractDate: "Data do contrato",
};

const PARTNER_FIELDS: Record<string, string> = { name: "nome", cpf: "CPF" };

/** Field names come from the schema of the type; unknown ones are shown as they are. */
export function fieldLabel(path: string): string {
  const partner = /^partners\[(\d+)\]\.(\w+)$/.exec(path);
  if (partner) {
    const [, position = "0", key = ""] = partner;
    return `Sócio ${Number(position) + 1}: ${PARTNER_FIELDS[key] ?? key}`;
  }

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
  DATE_VALID: "Data real de calendário, dentro do intervalo aceito para o campo",
  NO_LABEL_NEARBY: "Achado sem o rótulo ao lado; confiança reduzida",
  DOCUMENT_EXPIRED: "Documento com validade vencida",
  DATE_INVALID: "Data impossível ou fora do intervalo aceito para este campo",
  FORMAT_VALID: "Formato confere (sem dígito verificador para validar)",
  POSTAL_CODE_VALID: "CEP com oito dígitos",
  STATE_VALID: "Sigla de estado válida",
  CATEGORY_VALID: "Categoria de habilitação reconhecida",
  ADDRESS_BY_SHAPE: "Endereço achado pela forma da linha, sem rótulo",
  SERVICE_TYPE_KEYWORD: "Tipo de serviço identificado por palavra-chave",
  SERVICE_TYPE_AMBIGUOUS: "Mais de um tipo de serviço aparece no documento",
  FILIATION_ORDER_ASSUMED: "Ordem pai/mãe suposta: o documento traz só o bloco de filiação",
  MRZ_CHECK_VALID: "Dígitos verificadores da MRZ conferem",
  MRZ_CHECK_INVALID: "Algum dígito verificador da MRZ não confere",
  MRZ_BIRTH_DATE_MATCH: "Nascimento da MRZ confere com o impresso",
  MRZ_BIRTH_DATE_MISMATCH: "Nascimento da MRZ difere do impresso",
  MRZ_LENGTH_UNEXPECTED: "Linhas da MRZ com tamanho diferente de 30 caracteres",
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
export function statusTone(status: DocumentStatus): "pending" | "done" | "error" | "muted" {
  if (status === "PURGED") {
    return "muted";
  }

  if (status === "COMPLETED") {
    return "done";
  }

  if (status === "FAILED" || status === "REJECTED") {
    return "error";
  }

  return "pending";
}
