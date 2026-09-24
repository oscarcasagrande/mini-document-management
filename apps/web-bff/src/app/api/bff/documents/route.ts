import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";

/**
 * Upload and listing proxy. The browser posts here, the BFF forwards to the API marking the upload as
 * coming from the interface, and the problem document of a refusal is passed through untouched.
 */

export async function POST(request: Request): Promise<Response> {
  const correlationId = await currentCorrelationId();
  const contentType = request.headers.get("content-type");

  if (!contentType?.toLowerCase().includes("multipart/form-data")) {
    return NextResponse.json(
      {
        title: "Formato de requisição inválido",
        status: 415,
        detail: "O envio deve ser multipart/form-data.",
        correlationId,
      },
      { status: 415, headers: { [CORRELATION_HEADER]: correlationId } },
    );
  }

  const incoming = await request.formData();
  const file = incoming.get("file");

  if (!(file instanceof File) || file.size === 0) {
    return NextResponse.json(
      {
        title: "Arquivo ausente",
        status: 400,
        detail: "Selecione um arquivo antes de enviar.",
        correlationId,
      },
      { status: 400, headers: { [CORRELATION_HEADER]: correlationId } },
    );
  }

  const outgoing = new FormData();
  outgoing.set("file", file, file.name);

  const expectedDocumentType = incoming.get("expectedDocumentType");
  if (typeof expectedDocumentType === "string" && expectedDocumentType.trim().length > 0) {
    outgoing.set("expectedDocumentType", expectedDocumentType.trim());
  }

  const externalReference = incoming.get("externalReference");
  if (typeof externalReference === "string" && externalReference.trim().length > 0) {
    outgoing.set("externalReference", externalReference.trim());
  }

  const idempotencyKey = incoming.get("idempotencyKey");
  const forwardedHeaders: Record<string, string> = { "X-Upload-Channel": "WEB" };
  if (typeof idempotencyKey === "string" && idempotencyKey.trim().length > 0) {
    forwardedHeaders["Idempotency-Key"] = idempotencyKey.trim();
  }

  const response = await callApi({
    path: "/api/v1/documents",
    method: "POST",
    body: outgoing,
    headers: forwardedHeaders,
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: {
      [CORRELATION_HEADER]: correlationId,
      ...(response.headers.has("idempotency-replayed")
        ? { "Idempotency-Replayed": response.headers.get("idempotency-replayed") as string }
        : {}),
    },
  });
}

export async function GET(request: Request): Promise<Response> {
  const correlationId = await currentCorrelationId();
  const query = new URL(request.url).search;

  const response = await callApi({
    path: `/api/v1/documents${query}`,
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}
