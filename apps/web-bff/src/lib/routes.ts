/**
 * Paths served by this BFF. Client safe on purpose: the browser only ever addresses these, never the
 * API container, so this module must not import anything server only.
 */
export const bffRoutes = {
  upload: "/api/bff/documents",
  list: "/api/bff/documents",
  detail: (id: string) => `/api/bff/documents/${encodeURIComponent(id)}`,
  status: (id: string) => `/api/bff/documents/${encodeURIComponent(id)}/status`,
  content: (id: string) => `/api/bff/documents/${encodeURIComponent(id)}/content`,
  download: (id: string) => `/api/bff/documents/${encodeURIComponent(id)}/content?download=true`,
  reprocess: (id: string) => `/api/bff/documents/${encodeURIComponent(id)}/reprocess`,
};
