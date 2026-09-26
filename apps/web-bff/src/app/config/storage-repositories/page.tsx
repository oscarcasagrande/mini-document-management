import { AnonymousAccessBanner } from "@/components/AnonymousAccessBanner";
import { ConfigAdmin } from "@/components/ConfigAdmin";
import { ApiError } from "@/lib/api";
import { listConfig } from "@/lib/config";
import type { StorageRepository } from "@/lib/contracts";

export const dynamic = "force-dynamic";

export default async function StorageRepositoriesPage() {
  let items: StorageRepository[] = [];
  let failure: string | null = null;

  try {
    items = await listConfig<StorageRepository>("storage-repositories");
  } catch (error) {
    failure =
      error instanceof ApiError
        ? `${error.message} (correlationId ${error.correlationId})`
        : "Não foi possível falar com a API.";
  }

  return (
    <>
      <h1>Repositórios de armazenamento</h1>
      <p className="subtitle">
        Onde os arquivos originais ficam. Um documento vai para o repositório do seu produto ou serviço, ou para o padrão, e
        <strong> continua lá</strong> mesmo que o padrão mude depois. Exatamente um repositório é o padrão.
      </p>

      <AnonymousAccessBanner />

      <div className="alert alert--warning">
        <strong>Sistema de arquivos</strong> e <strong>banco de dados</strong> funcionam. <strong>Azure Blob</strong> e{" "}
        <strong>AWS S3</strong> podem ser cadastrados, mas ainda não têm adaptador: um envio para eles responde{" "}
        <span className="mono">501</span>, e eles não podem ser o padrão.
      </div>

      {failure && <div className="alert alert--error">{failure}</div>}

      <ConfigAdmin
        resource="storage-repositories"
        noun="repositório"
        emptyText="Nenhum repositório cadastrado."
        items={items}
        undeletable={{ key: "isDefault", value: true, reason: "O repositório padrão não pode ser excluído. Torne outro o padrão primeiro." }}
        fields={[
          {
            name: "code",
            label: "Código",
            kind: "text",
            required: true,
            createOnly: true,
            placeholder: "ARQUIVO-DB",
            help: "Único. Não muda depois de criado.",
          },
          { name: "name", label: "Nome", kind: "text", required: true, placeholder: "Arquivo no banco de dados" },
          {
            name: "provider",
            label: "Provedor",
            kind: "select",
            required: true,
            createOnly: true,
            defaultValue: "FILE_SYSTEM",
            options: [
              { value: "FILE_SYSTEM", label: "Sistema de arquivos" },
              { value: "DATABASE", label: "Banco de dados" },
              { value: "AZURE_BLOB_STORAGE", label: "Azure Blob Storage (ainda não implementado)" },
              { value: "AWS_S3", label: "AWS S3 (ainda não implementado)" },
            ],
            help: "Não muda depois de criado.",
          },
          {
            name: "connectionConfig",
            label: "Configuração de conexão (JSON, segredo)",
            kind: "secretJson",
            placeholder: '{"directory": "clientes/acme"}',
            help:
              "Nunca é exibida: só se sobrescreve. Ao editar, vale uma atualização parcial: informe só as chaves a mudar (null remove uma). " +
              "Sistema de arquivos: directory (opcional, relativo à raiz). Banco: nenhuma. Azure: connectionString e container. S3: bucket, accessKeyId, secretAccessKey.",
          },
          { name: "isDefault", label: "Repositório padrão", kind: "boolean", defaultValue: false },
          { name: "active", label: "Ativo", kind: "boolean", defaultValue: true },
        ]}
        columns={[
          { header: "Código", key: "code", kind: "mono" },
          { header: "Nome", key: "name" },
          { header: "Provedor", key: "provider" },
          { header: "Padrão", key: "isDefault", kind: "boolean" },
          { header: "Ativo", key: "active", kind: "boolean" },
          { header: "Configuração", key: "hasConnectionConfig", kind: "boolean" },
          { header: "Implementado", key: "isImplemented", kind: "boolean" },
        ]}
      />
    </>
  );
}
