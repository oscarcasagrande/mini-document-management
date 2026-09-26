import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";
import { isConfigResource } from "@/lib/config-resources";

/**
 * Proxy of the configuration resources (products, retention policies, storage repositories, webhooks).
 * Only the names listed in config-resources are forwarded, so the browser cannot use it to reach any
 * other path of the API.
 */

interface RouteContext {
  params: Promise<{ resource: string }>;
}

function unknownResource(correlationId: string): Response {
  return NextResponse.json(
    { title: "Recurso desconhecido", status: 404, detail: "Este recurso não existe.", correlationId },
    { status: 404, headers: { [CORRELATION_HEADER]: correlationId } },
  );
}

async function forward(request: Request, context: RouteContext, method: "GET" | "POST"): Promise<Response> {
  const { resource } = await context.params;
  const correlationId = await currentCorrelationId();

  if (!isConfigResource(resource)) {
    return unknownResource(correlationId);
  }

  const query = method === "GET" ? new URL(request.url).search : "";
  const response = await callApi({
    path: `/api/v1/${resource}${query}`,
    method,
    body: method === "POST" ? await request.text() : undefined,
    headers: method === "POST" ? { "Content-Type": "application/json" } : undefined,
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId },
  });
}

export const GET = (request: Request, context: RouteContext) => forward(request, context, "GET");

export const POST = (request: Request, context: RouteContext) => forward(request, context, "POST");
