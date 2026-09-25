"""Model profiles: the detector and recognizer are chosen by name, and a wrong name fails at startup."""

import pytest

from app.engine import DEFAULT_PROFILE, PROFILES, PaddleOcrEngine, resolve_profile
from app.settings import Settings


def test_default_profile_is_the_one_approved_in_the_adr():
    assert DEFAULT_PROFILE == "ppocrv5-mobile"
    assert Settings().model_profile == DEFAULT_PROFILE


def test_v5_profile_lets_paddleocr_pick_the_latin_recognizer():
    profile = resolve_profile("ppocrv5-mobile")

    assert profile.ocr_version == "PP-OCRv5"
    assert profile.detection_model == "PP-OCRv5_mobile_det"
    assert profile.recognition_model is None


@pytest.mark.parametrize("size", ["medium", "small", "tiny"])
def test_v6_profiles_pin_detector_and_recognizer_of_the_same_size(size: str):
    profile = resolve_profile(f"ppocrv6-{size}")

    assert profile.ocr_version == "PP-OCRv6"
    assert profile.detection_model == f"PP-OCRv6_{size}_det"
    assert profile.recognition_model == f"PP-OCRv6_{size}_rec"


def test_profile_names_are_their_own_keys():
    assert all(name == profile.name for name, profile in PROFILES.items())


def test_unknown_profile_lists_the_valid_ones():
    with pytest.raises(ValueError) as error:
        resolve_profile("ppocrv9-huge")

    assert "ppocrv9-huge" in str(error.value)
    assert "ppocrv5-mobile" in str(error.value)


def test_engine_rejects_an_unknown_profile_before_loading_anything():
    with pytest.raises(ValueError):
        PaddleOcrEngine(enable_mkldnn=True, model_cache_dir="/tmp/models", profile="nope")


def test_engine_reports_the_profile_before_it_is_loaded():
    engine = PaddleOcrEngine(enable_mkldnn=True, model_cache_dir="/tmp/models", profile="ppocrv6-tiny")

    assert "PP-OCRv6" in engine.model_version
    assert "not loaded" in engine.model_version


def test_profile_is_read_from_the_environment_in_any_case(monkeypatch: pytest.MonkeyPatch):
    monkeypatch.setenv("OCR_MODEL_PROFILE", " PPOCRV6-Small ")

    assert Settings.from_environment().model_profile == "ppocrv6-small"
