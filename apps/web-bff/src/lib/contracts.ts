/**
 * Mirror of the public API contracts of DocReader.Api.Contracts.V1. Kept hand written and small on
 * purpose: the BFF only needs the fields the interface renders.
 */

export type DocumentStatus =
  | "RECEIVED"
  | "STORED"
  | "QUEUED"
  | "PREPROCESSING"
  | "OCR_RUNNING"
  | "CLASSIFYING"
  | "EXTRACTING"
  | "COMPLETED"
  | "FAILED"
  | "REJECTED"
  | "PURGED";

export type UploadChannel = "WEB" | "API";

/** Product or service a document is linked to. */
export interface ProductServiceReference {
  id: string;
  code: string;
  name: string;
}

/** What a retention policy covers, from the most general to the most specific. */
export type RetentionScope = "GLOBAL" | "DOCUMENT_TYPE" | "PRODUCT_SERVICE" | "DOCUMENT_TYPE_AND_PRODUCT_SERVICE";

export interface RetentionPolicy {
  id: string;
  documentType: string | null;
  productService: ProductServiceReference | null;
  retentionDays: number;
  scope: RetentionScope;
  isGlobal: boolean;
  createdAt: string;
  updatedAt: string;
}

/** The purge date of a document and the policy that set it. */
export interface DocumentRetention {
  expiresAt: string;
  retentionDays: number;
  policy: {
    id: string;
    scope: RetentionScope;
    documentType: string | null;
    productServiceCode: string | null;
    retentionDays: number;
  } | null;
  purgedAt: string | null;
}

/** A webhook subscription. The signing secret is never returned by the API. */
export interface WebhookSubscription {
  id: string;
  url: string;
  events: string[];
  productService: ProductServiceReference | null;
  active: boolean;
  createdAt: string;
  updatedAt: string;
}

export type StorageProvider = "FILE_SYSTEM" | "DATABASE" | "AZURE_BLOB_STORAGE" | "AWS_S3";

/** A storage repository. The connection settings are secrets and never come from the API. */
export interface StorageRepository {
  id: string;
  code: string;
  name: string;
  provider: StorageProvider;
  isDefault: boolean;
  active: boolean;
  hasConnectionConfig: boolean;
  isImplemented: boolean;
  createdAt: string;
  updatedAt: string;
}

/** A product or service as administered in the registry. */
export interface ProductService extends ProductServiceReference {
  active: boolean;
  storageRepository: { id: string; code: string; name: string } | null;
  createdAt: string;
  updatedAt: string;
}

export interface DocumentLinks {
  self: string;
  status: string;
  content: string;
  download: string;
  text: string;
  result: string;
  reprocess: string;
}

export type ProcessingJobStatus = "PENDING" | "RUNNING" | "COMPLETED" | "FAILED";

/** Progress of the asynchronous work of a document; null when no job exists. */
export interface DocumentProcessing {
  jobStatus: ProcessingJobStatus;
  attempt: number;
  maxAttempts: number;
  pagesCompleted: number;
  pageCount: number | null;
  nextAttemptAt: string | null;
}

/** Answer of GET /documents/{id}/status, small enough to poll. */
export interface DocumentStatusSnapshot {
  id: string;
  protocol: string;
  status: DocumentStatus;
  lastError: { code: string; message: string } | null;
  processing: DocumentProcessing | null;
}

export interface DocumentSummary {
  id: string;
  protocol: string;
  fileName: string;
  mimeType: string;
  sizeBytes: number;
  pageCount: number;
  channel: UploadChannel;
  status: DocumentStatus;
  expectedDocumentType: string | null;
  detectedDocumentType: string | null;
  classificationConfidence: number | null;
  externalReference: string | null;
  productService: ProductServiceReference | null;
  expiresAt: string | null;
  uploadedAt: string;
  completedAt: string | null;
  links: DocumentLinks;
}

export interface DocumentTimelineEntry {
  eventType: string;
  stage: DocumentStatus;
  details: string | null;
  occurredAt: string;
}

export interface DocumentDetail {
  id: string;
  protocol: string;
  status: DocumentStatus;
  productService: ProductServiceReference | null;
  retention: DocumentRetention | null;
  upload: {
    fileName: string;
    channel: UploadChannel;
    uploadedAt: string;
    completedAt: string | null;
    externalReference: string | null;
    expectedDocumentType: string | null;
    mimeType: string;
    sizeBytes: number;
    pageCount: number;
    sha256: string;
  };
  classification: { detectedType: string; confidence: number | null } | null;
  extraction: {
    ocrProvider: string;
    ocrModelVersion: string;
    schemaVersion: number | null;
    overallConfidence: number | null;
    extractedAt: string;
  } | null;
  lastError: { code: string; message: string } | null;
  processing: DocumentProcessing | null;
  timeline: DocumentTimelineEntry[];
  links: DocumentLinks;
}

export type FieldValidationStatus = "VALID" | "INVALID" | "NOT_FOUND" | "UNCERTAIN";

export interface ExtractedField {
  raw: string | null;
  normalized: string | null;
  confidence: number | null;
  validationStatus: FieldValidationStatus;
  validationMessages: string[];
  evidence: { page: number | null; boundingBox: number[] };
}

/** Answer of GET /documents/{id}/result, the canonical result of section 15 of the PRD. */
export interface DocumentResult {
  id: string;
  protocol: string;
  status: DocumentStatus;
  classification: { detectedType: string; confidence: number | null; classifierVersion: string | null };
  extraction: {
    ocrProvider: string;
    ocrModelVersion: string;
    extractorVersion: string | null;
    schemaVersion: number | null;
    overallConfidence: number | null;
    extractedAt: string;
    fields: Record<string, ExtractedField>;
  };
}

/** Answer of GET /documents/{id}/text: the raw OCR text, page by page. */
export interface DocumentText {
  id: string;
  protocol: string;
  status: DocumentStatus;
  ocrProvider: string;
  ocrModelVersion: string;
  extractedAt: string;
  pages: { page: number; text: string }[];
}

export interface UploadAccepted {
  id: string;
  protocol: string;
  status: DocumentStatus;
  statusUrl: string;
  documentUrl: string;
  contentUrl: string;
}

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** RFC 9457 problem document, as produced by every error of the API. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  errorCode?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}

export const DOCUMENT_STATUSES: DocumentStatus[] = [
  "RECEIVED",
  "STORED",
  "QUEUED",
  "PREPROCESSING",
  "OCR_RUNNING",
  "CLASSIFYING",
  "EXTRACTING",
  "COMPLETED",
  "FAILED",
  "REJECTED",
  "PURGED",
];

/** Statuses whose document is still moving, so the interface keeps polling. */
export const IN_FLIGHT_STATUSES: ReadonlySet<DocumentStatus> = new Set<DocumentStatus>([
  "RECEIVED",
  "STORED",
  "QUEUED",
  "PREPROCESSING",
  "OCR_RUNNING",
  "CLASSIFYING",
  "EXTRACTING",
]);
