"""Provision the Challenge 1 knowledge source, knowledge base, and project connection.

This script converts the notebook workflow in create_knowledge_base.ipynb into a
single runnable Python entry point.
"""

from __future__ import annotations

import argparse
import logging
import os
from dataclasses import dataclass
from pathlib import Path

import requests
from azure.core.credentials import AzureKeyCredential
from azure.core.exceptions import ClientAuthenticationError, HttpResponseError
from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from azure.search.documents.indexes import SearchIndexClient
from azure.search.documents.indexes.models import (
    AzureBlobKnowledgeSource,
    AzureBlobKnowledgeSourceParameters,
    AzureOpenAIVectorizerParameters,
    KnowledgeBase,
    KnowledgeBaseAzureOpenAIModel,
    KnowledgeRetrievalLowReasoningEffort,
    KnowledgeRetrievalOutputMode,
    KnowledgeSourceAzureOpenAIVectorizer,
    KnowledgeSourceContentExtractionMode,
    KnowledgeSourceIngestionParameters,
    KnowledgeSourceReference,
)
from azure.search.documents.knowledgebases import KnowledgeBaseRetrievalClient
from azure.search.documents.knowledgebases.models import (
    KnowledgeBaseMessage,
    KnowledgeBaseMessageTextContent,
    KnowledgeBaseRetrievalRequest,
    SearchIndexKnowledgeSourceParams,
)
from dotenv import load_dotenv


LOGGER = logging.getLogger(__name__)
DEFAULT_KNOWLEDGE_SOURCE_NAME = "machine-wiki-blob-ks"
DEFAULT_KNOWLEDGE_BASE_NAME = "machine-kb"
DEFAULT_PROJECT_CONNECTION_NAME = "machine-wiki-connection"
DEFAULT_MACHINE_WIKI_CONTAINER = "machine-wiki"
DEFAULT_TEST_QUERY = "What can be the potential issue if curing_temperature is above 178°C"


@dataclass(frozen=True)
class Config:
    """Runtime configuration loaded from environment variables and CLI args."""

    storage_connection_string: str
    search_endpoint: str
    search_key: str
    model_deployment_name: str
    embedding_model_deployment_name: str
    openai_endpoint: str
    openai_key: str
    project_resource_id: str | None
    knowledge_source_name: str
    knowledge_base_name: str
    project_connection_name: str
    container_name: str
    test_query: str


def parse_args() -> argparse.Namespace:
    """Parse CLI arguments for the provisioning flow."""

    parser = argparse.ArgumentParser(
        description="Create the Challenge 1 knowledge source, knowledge base, and MCP connection.",
    )
    parser.add_argument(
        "--skip-test",
        action="store_true",
        help="Skip the knowledge base retrieval test step.",
    )
    parser.add_argument(
        "--skip-project-connection",
        action="store_true",
        help="Skip creation of the Azure AI project remote tool connection.",
    )
    parser.add_argument(
        "--knowledge-source-name",
        default=DEFAULT_KNOWLEDGE_SOURCE_NAME,
        help=f"Knowledge source name. Default: {DEFAULT_KNOWLEDGE_SOURCE_NAME}",
    )
    parser.add_argument(
        "--knowledge-base-name",
        default=DEFAULT_KNOWLEDGE_BASE_NAME,
        help=f"Knowledge base name. Default: {DEFAULT_KNOWLEDGE_BASE_NAME}",
    )
    parser.add_argument(
        "--project-connection-name",
        default=DEFAULT_PROJECT_CONNECTION_NAME,
        help=f"Project connection name. Default: {DEFAULT_PROJECT_CONNECTION_NAME}",
    )
    parser.add_argument(
        "--container-name",
        default=DEFAULT_MACHINE_WIKI_CONTAINER,
        help=f"Blob container name for the wiki content. Default: {DEFAULT_MACHINE_WIKI_CONTAINER}",
    )
    parser.add_argument(
        "--test-query",
        default=DEFAULT_TEST_QUERY,
        help="Question to send to the knowledge base during the validation step.",
    )
    return parser.parse_args()


def configure_logging() -> None:
    """Configure console logging for the script."""

    logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")


def load_environment() -> None:
    """Load environment variables from the repo root .env file when present."""

    repo_root = Path(__file__).resolve().parent.parent
    env_path = repo_root / ".env"
    load_dotenv(dotenv_path=env_path if env_path.exists() else None, override=True)


def build_config(args: argparse.Namespace) -> Config:
    """Build and validate runtime configuration."""

    config = Config(
        storage_connection_string=require_env("AZURE_STORAGE_CONNECTION_STRING"),
        search_endpoint=require_env("SEARCH_SERVICE_ENDPOINT").rstrip("/"),
        search_key=require_env("SEARCH_ADMIN_KEY"),
        model_deployment_name=require_env("MODEL_DEPLOYMENT_NAME"),
        embedding_model_deployment_name=require_env("EMBEDDING_MODEL_DEPLOYMENT_NAME"),
        openai_endpoint=require_env("AZURE_OPENAI_ENDPOINT"),
        openai_key=require_env("AZURE_OPENAI_KEY"),
        project_resource_id=os.environ.get("AZURE_AI_PROJECT_RESOURCE_ID"),
        knowledge_source_name=args.knowledge_source_name,
        knowledge_base_name=args.knowledge_base_name,
        project_connection_name=args.project_connection_name,
        container_name=args.container_name,
        test_query=args.test_query,
    )

    if not args.skip_project_connection and not config.project_resource_id:
        raise ValueError(
            "AZURE_AI_PROJECT_RESOURCE_ID is required unless --skip-project-connection is used."
        )

    return config


def require_env(name: str) -> str:
    """Return a required environment variable or raise a clear error."""

    value = os.environ.get(name)
    if not value:
        raise ValueError(f"Missing required environment variable: {name}")
    return value


def create_index_client(config: Config) -> SearchIndexClient:
    """Create the Azure AI Search index client."""

    return SearchIndexClient(
        endpoint=config.search_endpoint,
        credential=AzureKeyCredential(config.search_key),
    )


def create_or_update_knowledge_source(
    index_client: SearchIndexClient, config: Config
) -> None:
    """Create or update the blob-backed knowledge source."""

    knowledge_source = AzureBlobKnowledgeSource(
        name=config.knowledge_source_name,
        description="This knowledge source pulls from a blob storage container.",
        encryption_key=None,
        azure_blob_parameters=AzureBlobKnowledgeSourceParameters(
            connection_string=config.storage_connection_string,
            container_name=config.container_name,
            folder_path=None,
            is_adls_gen2=False,
            ingestion_parameters=KnowledgeSourceIngestionParameters(
                identity=None,
                disable_image_verbalization=False,
                chat_completion_model=KnowledgeBaseAzureOpenAIModel(
                    azure_open_ai_parameters=AzureOpenAIVectorizerParameters(
                        resource_url=config.openai_endpoint,
                        deployment_name=config.model_deployment_name,
                        api_key=config.openai_key,
                        model_name=config.model_deployment_name,
                    )
                ),
                embedding_model=KnowledgeSourceAzureOpenAIVectorizer(
                    azure_open_ai_parameters=AzureOpenAIVectorizerParameters(
                        resource_url=config.openai_endpoint,
                        deployment_name=config.embedding_model_deployment_name,
                        api_key=config.openai_key,
                        model_name=config.embedding_model_deployment_name,
                    )
                ),
                content_extraction_mode=KnowledgeSourceContentExtractionMode.MINIMAL,
                ingestion_schedule=None,
                ingestion_permission_options=None,
            ),
        ),
    )

    index_client.create_or_update_knowledge_source(knowledge_source)
    LOGGER.info("Knowledge source '%s' created or updated.", config.knowledge_source_name)


def create_or_update_knowledge_base(index_client: SearchIndexClient, config: Config) -> None:
    """Create or update the knowledge base that references the knowledge source."""

    aoai_params = AzureOpenAIVectorizerParameters(
        resource_url=config.openai_endpoint,
        api_key=config.openai_key,
        deployment_name=config.model_deployment_name,
        model_name=config.model_deployment_name,
    )

    knowledge_base = KnowledgeBase(
        name=config.knowledge_base_name,
        description="This knowledge base handles questions about common issues with manufacturing machines",
        retrieval_instructions=(
            f"Use the {config.knowledge_source_name} knowledge source to query "
            "potential root causes for problems by machine type."
        ),
        answer_instructions=(
            "Provide a single sentence for the likely cause of the issue based on the retrieved documents."
        ),
        output_mode=KnowledgeRetrievalOutputMode.ANSWER_SYNTHESIS,
        knowledge_sources=[KnowledgeSourceReference(name=config.knowledge_source_name)],
        models=[KnowledgeBaseAzureOpenAIModel(azure_open_ai_parameters=aoai_params)],
        encryption_key=None,
        retrieval_reasoning_effort=KnowledgeRetrievalLowReasoningEffort,
    )

    index_client.create_or_update_knowledge_base(knowledge_base)
    LOGGER.info("Knowledge base '%s' created or updated.", config.knowledge_base_name)


def test_knowledge_base(config: Config) -> str:
    """Query the knowledge base to verify retrieval works."""

    kb_client = KnowledgeBaseRetrievalClient(
        endpoint=config.search_endpoint,
        knowledge_base_name=config.knowledge_base_name,
        credential=AzureKeyCredential(config.search_key),
    )

    request = KnowledgeBaseRetrievalRequest(
        messages=[
            KnowledgeBaseMessage(
                role="user",
                content=[KnowledgeBaseMessageTextContent(text=config.test_query)],
            ),
        ],
        knowledge_source_params=[
            SearchIndexKnowledgeSourceParams(
                knowledge_source_name=config.knowledge_source_name,
                include_references=True,
                include_reference_source_data=True,
                always_query_source=False,
            )
        ],
        include_activity=True,
    )

    result = kb_client.retrieve(request)
    response_text = result.response[0].content[0].text
    LOGGER.info("Knowledge base test response: %s", response_text)
    return response_text


def create_or_update_project_connection(config: Config) -> None:
    """Create or update the Azure AI project remote tool connection for the knowledge base."""

    if not config.project_resource_id:
        raise ValueError("AZURE_AI_PROJECT_RESOURCE_ID is required to create the project connection.")

    credential = DefaultAzureCredential()
    bearer_token_provider = get_bearer_token_provider(
        credential,
        "https://management.azure.com/.default",
    )
    mcp_endpoint = (
        f"{config.search_endpoint}/knowledgebases/{config.knowledge_base_name}"
        "/mcp?api-version=2025-11-01-preview"
    )

    response = requests.put(
        (
            "https://management.azure.com"
            f"{config.project_resource_id}/connections/{config.project_connection_name}"
            "?api-version=2025-10-01-preview"
        ),
        headers={"Authorization": f"Bearer {bearer_token_provider()}"},
        json={
            "name": config.project_connection_name,
            "type": "Microsoft.MachineLearningServices/workspaces/connections",
            "properties": {
                "authType": "ProjectManagedIdentity",
                "category": "RemoteTool",
                "target": mcp_endpoint,
                "isSharedToAll": True,
                "audience": "https://search.azure.com/",
                "metadata": {"ApiType": "Azure"},
            },
        },
        timeout=60,
    )
    response.raise_for_status()
    LOGGER.info("Project connection '%s' created or updated.", config.project_connection_name)


def main() -> int:
    """Run the full provisioning flow."""

    configure_logging()
    args = parse_args()
    load_environment()
    config = build_config(args)
    index_client = create_index_client(config)

    create_or_update_knowledge_source(index_client, config)
    create_or_update_knowledge_base(index_client, config)

    if not args.skip_test:
        test_knowledge_base(config)

    if not args.skip_project_connection:
        create_or_update_project_connection(config)

    LOGGER.info("Provisioning complete.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, HttpResponseError, ClientAuthenticationError, requests.RequestException) as error:
        LOGGER.error("Provisioning failed: %s", error)
        raise SystemExit(1) from error