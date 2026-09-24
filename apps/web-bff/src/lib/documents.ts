import { fetchJson } from "./api";
import type { DocumentDetail, DocumentSummary, PagedResponse } from "./contracts";

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

export function getDocumentByProtocol(protocol: string): Promise<DocumentDetail> {
  return fetchJson<DocumentDetail>(`/api/v1/documents/by-protocol/${encodeURIComponent(protocol)}`);
}
