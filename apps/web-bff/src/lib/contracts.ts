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
  | "REJECTED";

export type UploadChannel = "WEB" | "API";

export interface DocumentLinks {
  self: string;
  status: string;
  content: string;
  download: string;
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
  extraction: { ocrProvider: string; schemaVersion: number; overallConfidence: number | null } | null;
  lastError: { code: string; message: string } | null;
  timeline: DocumentTimelineEntry[];
  links: DocumentLinks;
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
