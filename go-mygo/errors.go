package main

import "fmt"

type BridgeError struct {
	Code    string
	Message string
}

func (e *BridgeError) Error() string { return e.Message }

func bridgeErr(code, message string) error {
	return &BridgeError{Code: code, Message: message}
}

func errorCode(err error) string {
	if err == nil {
		return ""
	}
	if value, ok := err.(*BridgeError); ok {
		return value.Code
	}
	return "INTERNAL_ERROR"
}

func errorMessage(err error) string {
	if err == nil {
		return ""
	}
	if value, ok := err.(*BridgeError); ok {
		return value.Message
	}
	return fmt.Sprintf("内部错误：%v", err)
}
