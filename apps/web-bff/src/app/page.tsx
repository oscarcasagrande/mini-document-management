import Link from "next/link";

import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { UploadForm } from "@/components/UploadForm";
import { listConfig } from "@/lib/config";
import type { ProductService } from "@/lib/contracts";

const swaggerUrl = process.env.DOCREADER_PUBLIC_SWAGGER_URL ?? "http://localhost:8080/swagger";

const maxSizeBytes = Number(process.env.DOCREADER_MAX_UPLOAD_BYTES ?? 26_214_400);
const maxPageCount = Number(process.env.DOCREADER_MAX_PAGE_COUNT ?? 50);

export const dynamic = "force-dynamic";

/** The registry is only a convenience for the form: without it the upload still works, just with no product to pick. */
async function activeProducts(): Promise<ProductService[]> {
  try {
    return (await listConfig<ProductService>("product-services")).filter((product) => product.active);
  } catch {
    return [];
  }
}

export default async function HomePage() {
  const products = await activeProducts();

  return (
    <>
      <h1>Leitura de documentos</h1>
      <p className="subtitle">
        Envie um documento, receba um protocolo e acompanhe o processamento. Leitura local: OCR,
        classificação e extração de campos, sem enviar nada para fora.
      </p>

      <AnonymousAccessBanner />

      <UploadForm maxSizeBytes={maxSizeBytes} maxPageCount={maxPageCount} products={products} />

      <section className="card">
        <h2 className="card__title">Onde continuar</h2>
        <p className="card__hint">
          O upload responde de imediato com protocolo; o OCR roda fora da requisição.
        </p>
        <ul>
          <li>
            <Link href="/documents">Lista de documentos</Link> com filtros, paginação e atualização
            automática.
          </li>
          <li>
            <a href={swaggerUrl} target="_blank" rel="noreferrer">
              Swagger da API
            </a>{" "}
            para enviar e consultar pelos endpoints, sem login.
          </li>
        </ul>
        <p className="stage-note">
          O documento passa por <strong>Na fila</strong>, leitura, classificação e extração até ficar
          <strong>Concluído</strong>; se o OCR falhar, o arquivo original continua consultável. Os tipos
          com campos estruturados são CPF, CIN/RG, CNH, comprovante de residência, cartão CNPJ, CCMEI e
          contrato social.
        </p>
      </section>
    </>
  );
}
