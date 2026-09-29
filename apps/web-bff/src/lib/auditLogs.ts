import { fetchJson } from "./api";
import type { AuditLog, PagedResponse } from "./contracts";

/** Filters accepted by the audit log listing page, mirroring the query string of the API. */
export interface AuditLogFilters {
  userId?: string;
  action?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export function toQueryString(filters: AuditLogFilters): string {
  const query = new URLSearchParams();

  const append = (key: string, value: string | number | undefined) => {
    if (value === undefined || value === "" || value === null) {
      return;
    }
    query.set(key, String(value));
  };

  append("userId", filters.userId);
  append("action", filters.action);
  // The API expects instants; a date picker gives a day, so the bounds cover the whole day in UTC.
  append("from", filters.from ? `${filters.from}T00:00:00Z` : undefined);
  append("to", filters.to ? `${filters.to}T23:59:59Z` : undefined);
  append("page", filters.page);
  append("pageSize", filters.pageSize);

  const text = query.toString();
  return text.length > 0 ? `?${text}` : "";
}

export function listAuditLogs(filters: AuditLogFilters): Promise<PagedResponse<AuditLog>> {
  return fetchJson<PagedResponse<AuditLog>>(`/api/v1/audit-logs${toQueryString(filters)}`);
}
