import { ApiError, fetchJson } from "./api";
import type { DocumentDetail, DocumentResult, DocumentSummary, DocumentText, PagedResponse } from "./contracts";

/** Filters accepted by the listing page, mirroring the query string of the API. */
export interface DocumentFilters {
  protocol?: string;
  fileName?: string;
  documentType?: string;
  channel?: string;
  status?: string;
  uploadedFrom?: string;
  uploadedTo?: string;
  page?: number;
  pageSize?: number;
}

export function toQueryString(filters: DocumentFilters): string {
  const query = new URLSearchParams();

  const append = (key: string, value: string | number | undefined) => {
    if (value === undefined || value === "" || value === null) {
      return;
    }
    query.set(key, String(value));
  };

  append("protocol", filters.protocol);
  append("fileName", filters.fileName);
  append("documentType", filters.documentType);
  append("channel", filters.channel);
  append("status", filters.status);
  // The API expects instants; a date picker gives a day, so the bounds cover the whole day in UTC.
  append("uploadedFrom", filters.uploadedFrom ? `${filters.uploadedFrom}T00:00:00Z` : undefined);
  append("uploadedTo", filters.uploadedTo ? `${filters.uploadedTo}T23:59:59Z` : undefined);
  append("page", filters.page);
  append("pageSize", filters.pageSize);

  const text = query.toString();
  return text.length > 0 ? `?${text}` : "";
}

export function listDocuments(filters: DocumentFilters): Promise<PagedResponse<DocumentSummary>> {
  return fetchJson<PagedResponse<DocumentSummary>>(`/api/v1/documents${toQueryString(filters)}`);
}

export function getDocument(id: string): Promise<DocumentDetail> {
  return fetchJson<DocumentDetail>(`/api/v1/documents/${encodeURIComponent(id)}`);
}

/**
 * The API answers 409 while a document has no result yet, which is a normal state for the page and
 * not an error: it maps to null so the page can say "not read yet".
 */
async function nullWhenNotReady<T>(request: Promise<T>): Promise<T | null> {
  try {
    return await request;
  } catch (error) {
    if (error instanceof ApiError && error.status === 409) {
      return null;
    }
    throw error;
  }
}

export function getDocumentResult(id: string): Promise<DocumentResult | null> {
  return nullWhenNotReady(fetchJson<DocumentResult>(`/api/v1/documents/${encodeURIComponent(id)}/result`));
}

export function getDocumentText(id: string): Promise<DocumentText | null> {
  return nullWhenNotReady(fetchJson<DocumentText>(`/api/v1/documents/${encodeURIComponent(id)}/text`));
}

export function getDocumentByProtocol(protocol: string): Promise<DocumentDetail> {
  return fetchJson<DocumentDetail>(`/api/v1/documents/by-protocol/${encodeURIComponent(protocol)}`);
}
