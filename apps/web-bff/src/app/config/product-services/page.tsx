import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { ConfigAdmin } from "@/components/ConfigAdmin";
import { ApiError } from "@/lib/api";
import { listConfig } from "@/lib/config";
import type { ProductService, StorageRepository } from "@/lib/contracts";

export const dynamic = "force-dynamic";

export default async function ProductServicesPage() {
  let items: ProductService[] = [];
  let repositories: StorageRepository[] = [];
  let failure: string | null = null;

  try {
    [items, repositories] = await Promise.all([
      listConfig<ProductService>("product-services"),
      listConfig<StorageRepository>("storage-repositories"),
    ]);
  } catch (error) {
    failure =
      error instanceof ApiError
        ? `${error.message} (correlationId ${error.correlationId})`
        : "Não foi possível falar com a API.";
  }

  return (
    <>
      <h1>Produtos e serviços</h1>
      <p className="subtitle">
        Um documento pode ser enviado para um produto ou serviço, pelo código. O código é único e não muda depois de
        criado; um produto inativo deixa de aceitar novos envios.
      </p>

      <AnonymousAccessBanner />

      {failure && <div className="alert alert--error">{failure}</div>}

      <ConfigAdmin
        resource="product-services"
        noun="produto ou serviço"
        emptyText="Nenhum produto ou serviço cadastrado."
        items={items}
        fields={[
          {
            name: "code",
            label: "Código",
            kind: "text",
            required: true,
            createOnly: true,
            placeholder: "CONTA-PJ",
            help: "Letras, números, ponto, hífen ou sublinhado. Guardado em maiúsculas.",
          },
          { name: "name", label: "Nome", kind: "text", required: true, placeholder: "Abertura de conta PJ" },
          {
            name: "storageRepositoryId",
            valuePath: "storageRepository.id",
            label: "Repositório de armazenamento",
            kind: "select",
            nullable: true,
            options: [
              { value: "", label: "Padrão do sistema" },
              ...repositories
                .filter((repository) => repository.active)
                .map((repository) => ({ value: repository.id, label: `${repository.code} · ${repository.name}` })),
            ],
            help: "Onde ficam os arquivos dos documentos deste produto. Documentos já enviados não mudam de lugar.",
          },
          { name: "active", label: "Ativo", kind: "boolean", defaultValue: true },
        ]}
        columns={[
          { header: "Código", key: "code", kind: "mono" },
          { header: "Nome", key: "name" },
          { header: "Ativo", key: "active", kind: "boolean" },
          { header: "Repositório", key: "storageRepository.code", kind: "mono", empty: "padrão" },
          { header: "Criado em", key: "createdAt", kind: "instant" },
        ]}
      />
    </>
  );
}
