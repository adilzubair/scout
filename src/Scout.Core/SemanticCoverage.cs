namespace Scout.Core;

public static class SemanticCoverage
{
    public static string Describe(string detail)
    {
        var reasons=detail.Split(';')[0].Split(',',StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ",reasons.Select(reason=>reason switch
        {
            "legacy_office_format"=>"Legacy Office contents are not supported.",
            "macro_enabled_office_format"=>"Macro-enabled Office files are not processed.",
            "office_variant_unavailable"=>"This Office template or slideshow variant is not supported.",
            "archive_contents_unavailable"=>"Archive contents are not opened or indexed.",
            "encrypted_pdf" or "encrypted_odf" or "encrypted_archive_entry"=>"Encrypted contents cannot be read.",
            "binary_office_container"=>"Encrypted or binary Office contents cannot be read.",
            "active_or_encrypted_package"=>"The package contains active or encrypted components.",
            "image_decoder_unavailable"=>"This image format has no enabled reader.",
            "media_container_unavailable"=>"This media container has no enabled reader.",
            "media_decoder_unavailable"=>"This media codec has no enabled decoder.",
            "malformed_image" or "malformed_package" or "malformed_xml" or "malformed_pdf" or "malformed_media"=>"This file is malformed or incomplete and could not be decoded.",
            "unsupported_text_encoding"=>"This text encoding is not supported; save as UTF-8 or UTF-16.",
            "image_pixel_budget" or "video_pixel_budget"=>"This image or frame exceeds the pixel limit.",
            "source_size_limit"=>"This file exceeds the content size limit.",
            "office_visual_layout_unavailable"=>"Office layout is not reproduced; available text, data and images are indexed.",
            "formula_cache_unavailable"=>"Some formulas have no saved result; formulas are not run.",
            "chart_cache_unavailable"=>"Some chart values have no saved cache.",
            "drawing_position_unavailable"=>"Some drawing positions are unavailable; sheet content is retained.",
            "external_resource_unavailable"=>"External resources are not fetched.",
            "embedded_object_unavailable" or "embedded_image_format"=>"Some embedded objects or images have no enabled reader.",
            "ocr_language_unavailable" or "ocr_unavailable"=>"OCR is unavailable for a selected language; visual content is retained.",
            "ocr_time_limit" or "ocr_page_limit" or "ocr_text_limit"=>"OCR reached a limit; visual content is retained.",
            "ocr_failed"=>"Some text recognition failed; visual content is retained.",
            "page_limit" or "segment_limit" or "prepared_output_limit"=>"The content read reached a page, segment or output limit.",
            "line_limit" or "sheet_row_limit" or "sheet_cell_limit"=>"The content read reached a line or cell limit.",
            "embedded_image_size"=>"Some embedded images exceed the size limit.",
            "video_frames_sampled"=>"Video vision uses sampled frames, not every frame.",
            "additional_audio_tracks_not_indexed" or "additional_video_tracks_not_indexed"=>"Only the first audio and video tracks are indexed.",
            "audio_track_not_indexed"=>"The audio track could not be indexed.",
            "media_duration_limit" or "video_window_coverage_limit"=>"The media read reached its duration or window limit.",
            "audio_timeline_gap" or "video_timeline_gap"=>"Some media timeline intervals have no decoded samples.",
            "video_dimensions_changed" or "video_end_estimated"=>"Some video coverage is estimated or has changing dimensions.",
            "audio_peak_clipped"=>"Audio peaks were clipped for the model input.",
            "access_denied"=>"File access was denied.",
            "read_or_decode_failed"=>"The file could not be read or decoded. Check access and retry.",
            "unsupported_content_format" or "no_extractable_content"=>"No enabled reader can extract this content.",
            _=>"Some content is unavailable. Open the original for full contents."
        }).Distinct());
    }
}
