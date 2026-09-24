import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";

interface RouteContext {
  params: Promise<{ id: string }>;
}

/** Headers copied from the API answer so the browser renders or downloads exactly what it sent. */
const FORWARDED_HEADERS = [
  "content-type",
  "content-length",
  "content-disposition",
  "accept-ranges",
  "etag",
  "last-modified",
];

/**
 * Streams the original file through the BFF. The response body is piped, never buffered, so a large
 * PDF does not sit in memory, and the browser keeps talking only to this origin.
 */
export async function GET(request: Request, context: RouteContext): Promise<Response> {
  const { id } = await context.params;
  const correlationId = await currentCorrelationId();
  const download = new URL(request.url).searchParams.get("download") === "true";

  const range = request.headers.get("range");

  const response = await callApi({
    path: `/api/v1/documents/${encodeURIComponent(id)}/content${download ? "?download=true" : ""}`,
    correlationId,
    headers: range ? { Range: range } : undefined,
  });

  if (!response.ok && response.status !== 206) {
    return NextResponse.json(await readProblem(response), {
      status: response.status,
      headers: { [CORRELATION_HEADER]: correlationId },
    });
  }

  const headers = new Headers({
    [CORRELATION_HEADER]: correlationId,
    "X-Content-Type-Options": "nosniff",
    "Cache-Control": "private, max-age=0, must-revalidate",
  });

  for (const name of FORWARDED_HEADERS) {
    const value = response.headers.get(name);
    if (value !== null) {
      headers.set(name, value);
    }
  }

  return new NextResponse(response.body, { status: response.status, headers });
}
