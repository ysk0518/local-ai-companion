import io
import os
import tempfile
import unittest
from unittest.mock import patch

from local_ai_companion.cli import build_parser, run_once, main, _make_log_writer
from local_ai_companion.config import AppConfig


class CLITests(unittest.TestCase):
    def test_parser_accepts_message(self):
        parser = build_parser()
        args = parser.parse_args(["--message", "hello"])
        self.assertEqual(args.message, "hello")

    def test_parser_accepts_conversation_id(self):
        parser = build_parser()
        args = parser.parse_args(["--conversation-id", "session-1", "--message", "hi"])
        self.assertEqual(args.conversation_id, "session-1")

    def test_parser_accepts_request_id(self):
        parser = build_parser()
        args = parser.parse_args(["--request-id", "req-123", "--message", "hi"])
        self.assertEqual(args.request_id, "req-123")

    def test_parser_accepts_log_dir(self):
        parser = build_parser()
        args = parser.parse_args(["--log-dir", "logs", "--message", "hi"])
        self.assertEqual(args.log_dir, "logs")

    def test_parser_defaults_are_none(self):
        parser = build_parser()
        args = parser.parse_args(["--message", "hi"])
        self.assertIsNone(args.conversation_id)
        self.assertIsNone(args.request_id)
        self.assertIsNone(args.log_dir)

    def test_run_once_without_log_dir(self):
        config = AppConfig()
        exit_code = run_once(config, "session", "req-1", "hello", None)
        self.assertEqual(exit_code, 0)

    def test_run_once_with_log_dir(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            from local_ai_companion.log_writer import JSONLLogWriter
            config = AppConfig()
            writer = JSONLLogWriter(tmpdir)
            exit_code = run_once(config, "session", "req-1", "hello", writer)
            self.assertEqual(exit_code, 0)
            log_path = os.path.join(tmpdir, "conversation.jsonl")
            self.assertTrue(os.path.isfile(log_path))

    def test_run_once_uses_request_id_in_output(self):
        config = AppConfig()
        old_stdout = sys.stdout
        try:
            buf = io.StringIO()
            sys.stdout = buf
            run_once(config, "c1", "req-xyz", "hello", None)
            output = buf.getvalue()
            self.assertIn("req-xyz", output)
            self.assertIn("c1", output)
        finally:
            sys.stdout = old_stdout


import sys  # noqa: E402 (needed for test_run_once_uses_request_id_in_output above)


class MakeLogWriterTests(unittest.TestCase):
    def test_with_log_dir_arg(self):
        config = AppConfig()
        with tempfile.TemporaryDirectory() as tmpdir:
            log_dir = os.path.join(tmpdir, "test_logs")
            writer = _make_log_writer(config, log_dir)
            self.assertIsNotNone(writer)
            from local_ai_companion.log_writer import JSONLLogWriter
            self.assertIsInstance(writer, JSONLLogWriter)

    def test_with_config_logging_enabled(self):
        from local_ai_companion.config import LoggingConfig
        with tempfile.TemporaryDirectory() as tmpdir:
            log_dir = os.path.join(tmpdir, "test_config_logs")
            config = AppConfig(logging=LoggingConfig(enabled=True, log_dir=log_dir))
            writer = _make_log_writer(config, None)
            self.assertIsNotNone(writer)

    def test_disabled_returns_none(self):
        config = AppConfig()
        writer = _make_log_writer(config, None)
        self.assertIsNone(writer)

    def test_log_dir_arg_overrides_config(self):
        from local_ai_companion.config import LoggingConfig
        with tempfile.TemporaryDirectory() as tmpdir:
            config_dir = os.path.join(tmpdir, "from_config")
            arg_dir = os.path.join(tmpdir, "from_arg")
            config = AppConfig(logging=LoggingConfig(enabled=True, log_dir=config_dir))
            writer = _make_log_writer(config, arg_dir)
            self.assertIsNotNone(writer)


class MainTests(unittest.TestCase):
    def test_main_with_message(self):
        with patch("sys.stdout", new_callable=io.StringIO) as mock_stdout:
            exit_code = main(["--message", "hello", "--request-id", "req-main"])
            self.assertEqual(exit_code, 0)
            output = mock_stdout.getvalue()
            self.assertIn("req-main", output)
            self.assertIn("assistant", output)

    def test_main_with_serve_flag(self):
        with patch("local_ai_companion.server.run_server") as mock_run:
            exit_code = main(["--serve"])
            self.assertEqual(exit_code, 0)
            mock_run.assert_called_once()

    def test_main_without_message_uses_repl(self):
        # Simulate REPL by providing a single input then EOF
        with patch("sys.stdout", new_callable=io.StringIO):
            with patch("builtins.input", side_effect=["hello from repl", EOFError]):
                exit_code = main([])
                self.assertEqual(exit_code, 0)

    def test_main_with_custom_conversation_id(self):
        with patch("sys.stdout", new_callable=io.StringIO) as mock_stdout:
            exit_code = main(["--message", "hi", "--conversation-id", "custom-session"])
            self.assertEqual(exit_code, 0)
            output = mock_stdout.getvalue()
            self.assertIn("custom-session", output)


if __name__ == "__main__":
    unittest.main()
