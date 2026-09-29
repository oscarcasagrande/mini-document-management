import { NextResponse } from "next/server";

import { CORRELATION_HEADER, callApi, currentCorrelationId, readProblem } from "@/lib/api";
import { isConfigResource } from "@/lib/config-resources";

/** Reveals the decrypted connection settings of one resource (today only storage-repositories has this). */

interface RouteContext {
  params: Promise<{ resource: string; id: string }>;
}

export async function GET(_request: Request, context: RouteContext): Promise<Response> {
  const { resource, id } = await context.params;
  const correlationId = await currentCorrelationId();

  if (!isConfigResource(resource)) {
    return NextResponse.json(
      { title: "Recurso desconhecido", status: 404, detail: "Este recurso não existe.", correlationId },
      { status: 404, headers: { [CORRELATION_HEADER]: correlationId } },
    );
  }

  const response = await callApi({
    path: `/api/v1/${resource}/${encodeURIComponent(id)}/connection-config`,
    method: "GET",
    correlationId,
  });

  const payload = response.ok ? await response.json() : await readProblem(response);

  return NextResponse.json(payload, {
    status: response.status,
    headers: { [CORRELATION_HEADER]: correlationId, "Cache-Control": "no-store" },
  });
}
